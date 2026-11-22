using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Raven.Client.Documents;
using Raven.Client.Documents.Attachments;
using Raven.Client.Documents.Operations.Attachments;
using Raven.Client.Exceptions;
using SkiaSharp;

public static class LibraryHandler
{
    public const long MaxCoverBytes = 5 * 1024 * 1024;
    public const long MaxAddRequestBytes = MaxCoverBytes + 64 * 1024;

    public static async Task<Results<Ok<CatalogEdition[]>, ValidationProblem, ProblemHttpResult>> SearchCatalog(
        string? q, OpenLibraryClient catalog, IDocumentStore store, CancellationToken ct)
    {
        var query = q?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > 200)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Enter a title, author, or ISBN (up to 200 characters)."] });

        CatalogEdition[] editions;
        try { editions = await catalog.SearchAsync(query, ct); }
        catch (CatalogUnavailableException ex)
        {
            return TypedResults.Problem(title: ex.Message, statusCode: 503);
        }

        if (editions.Length == 0) return TypedResults.Ok(editions);
        using var session = store.OpenAsyncSession();
        var saved = await session.LoadAsync<LibraryBook>(editions.Select(e => LibraryBook.DocumentId(e.EditionId)), ct);
        return TypedResults.Ok(editions.Select(e => e with { AlreadyAdded = saved[LibraryBook.DocumentId(e.EditionId)] is not null }).ToArray());
    }

    public static async Task<Ok<IReadOnlyList<GalleryBook>>> GetBooks(IDocumentStore store, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var books = await session.Query<LibraryBook>()
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .OrderByDescending(x => x.AddedAt)
            .ThenBy(x => x.EditionId)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<GalleryBook>>(books.Select(x => x.ToGallery()).ToArray());
    }

    public static async Task<Results<Ok<GalleryBook>, JsonHttpResult<GalleryBook>, ProblemHttpResult>> AddBook(
        [FromForm] AddLibraryBookRequest input,
        HttpRequest request,
        IDocumentStore store,
        OpenLibraryClient catalog,
        CancellationToken cancellationToken)
    {
        var editionId = input.EditionId.Trim();
        if (!LibraryBook.IsValidEditionId(editionId))
            return TypedResults.Problem("A valid Open Library edition ID is required.", statusCode: 400);

        var documentId = LibraryBook.DocumentId(editionId);
        using (var checkSession = store.OpenAsyncSession())
        {
            var existing = await checkSession.LoadAsync<LibraryBook>(documentId, cancellationToken);
            if (existing is not null)
                return TypedResults.Ok(existing.ToGallery());
        }

        var snapshot = catalog.FindSearchedEdition(editionId);
        if (snapshot is null)
            return TypedResults.Problem("Search for this edition again, then add it.", statusCode: 404);

        var cover = input.Cover;
        var files = request.Form.Files; // Form binding has already read and cached the form.
        ValidatedCover? validated = null;
        if (files.Count > 1 || (files.Count == 1 && cover is null))
            return TypedResults.Problem("Supply at most one cover image.", statusCode: 400);
        if (cover is not null)
        {
            var result = await ValidateCoverAsync(cover, cancellationToken);
            if (!result.IsValid)
                return TypedResults.Problem(result.Error, statusCode: result.StatusCode);
            validated = result.Cover!;
        }

        using var coverBytes = validated?.Bytes;
        var book = LibraryBook.From(snapshot.Edition, snapshot.WorkId, validated is not null);
        using var session = store.OpenAsyncSession();
        // An empty change vector means the document must not already exist.
        await session.StoreAsync(book, string.Empty, documentId, cancellationToken);
        if (validated is not null)
        {
            // Keep this stream alive until SaveChangesAsync has consumed it.
            session.Advanced.Attachments.Store(documentId, LibraryBook.CoverAttachmentName,
                validated.Bytes, validated.ContentType);
        }

        try
        {
            await session.SaveChangesAsync(cancellationToken);
            return TypedResults.Json(book.ToGallery(), statusCode: StatusCodes.Status201Created);
        }
        catch (ConcurrencyException)
        {
            using var winnerSession = store.OpenAsyncSession();
            var winner = await winnerSession.LoadAsync<LibraryBook>(documentId, cancellationToken);
            return winner is null
                ? TypedResults.Problem("The library write conflicted and could not be read back.", statusCode: 409)
                : TypedResults.Ok(winner.ToGallery());
        }
    }

    public static async Task<Results<FileStreamHttpResult, NotFound>> GetCover(
        string editionId, IDocumentStore store, CancellationToken cancellationToken)
    {
        if (!LibraryBook.IsValidEditionId(editionId)) return TypedResults.NotFound();
        var attachment = await store.Operations.SendAsync(new GetAttachmentOperation(
            LibraryBook.DocumentId(editionId), LibraryBook.CoverAttachmentName, AttachmentType.Document, null), token: cancellationToken);
        if (attachment is null) return TypedResults.NotFound();
        return TypedResults.Stream(attachment.Stream, attachment.Details.ContentType);
    }

    private static async Task<CoverValidationResult> ValidateCoverAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length > MaxCoverBytes) return CoverValidationResult.Invalid("Cover must be 5 MiB or smaller.", 413);
        if (file.Length == 0) return CoverValidationResult.Invalid("The cover is empty.", 400);
        await using var input = file.OpenReadStream();
        using var bytes = new MemoryStream(capacity: checked((int)file.Length));
        await input.CopyToAsync(bytes, cancellationToken);
        var image = bytes.ToArray();
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data);
        if (codec is null) return CoverValidationResult.Invalid("The cover is not a valid image.", 400);
        var type = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => null
        };
        if (type is null) return CoverValidationResult.Invalid("Only JPEG, PNG, and WebP covers are accepted.", 415);
        if (codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
            codec.Info.Width > 6000 || codec.Info.Height > 6000 ||
            (long)codec.Info.Width * codec.Info.Height > 12_000_000)
            return CoverValidationResult.Invalid("The cover dimensions are too large.", 400);
        using var decoded = new SKBitmap(codec.Info);
        if (decoded.GetPixels() == IntPtr.Zero || codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            return CoverValidationResult.Invalid("The cover is not a complete image.", 400);
        return CoverValidationResult.Valid(new MemoryStream(image, writable: false), type);
    }

    private sealed record CoverValidationResult(ValidatedCover? Cover, string? Error, int StatusCode)
    {
        public bool IsValid => Cover is not null;
        public static CoverValidationResult Valid(MemoryStream bytes, string contentType) => new(new ValidatedCover(bytes, contentType), null, 200);
        public static CoverValidationResult Invalid(string error, int statusCode) => new(null, error, statusCode);
    }
    private sealed record ValidatedCover(MemoryStream Bytes, string ContentType);
}

public sealed class AddLibraryBookRequest
{
    [FromForm(Name = "editionId")]
    [Required]
    public string EditionId { get; set; } = "";

    [FromForm(Name = "cover")]
    public IFormFile? Cover { get; set; }
}
