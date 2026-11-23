using System.Security.Claims;
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
        string? q, ClaimsPrincipal user, OpenLibraryClient catalog, IDocumentStore store, CancellationToken ct)
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
        var ownerKey = LibraryAuthentication.OwnerKey(user);
        using var session = store.OpenAsyncSession();
        var saved = await session.LoadAsync<LibraryEntry>(editions.Select(e => LibraryEntry.DocumentId(ownerKey, e.EditionId)), ct);
        return TypedResults.Ok(editions.Select(e => e with { AlreadyAdded = saved[LibraryEntry.DocumentId(ownerKey, e.EditionId)] is not null }).ToArray());
    }

    public static async Task<Ok<IReadOnlyList<GalleryBook>>> GetBooks(ClaimsPrincipal user, IDocumentStore store, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var ownerKey = LibraryAuthentication.OwnerKey(user);
        var entries = await session.Query<LibraryEntry>()
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(x => x.OwnerKey == ownerKey)
            .OrderByDescending(x => x.AddedAt)
            .ThenBy(x => x.BookId)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0) return TypedResults.Ok<IReadOnlyList<GalleryBook>>([]);
        var books = await session.LoadAsync<LibraryBook>(entries.Select(x => x.BookId), cancellationToken);
        return TypedResults.Ok<IReadOnlyList<GalleryBook>>(entries
            .Where(x => books[x.BookId] is not null).Select(x => x.ToGallery(books[x.BookId])).ToArray());
    }

    public static async Task<Results<Ok<GalleryBook>, JsonHttpResult<GalleryBook>, ProblemHttpResult>> AddBook(
        [FromForm] AddLibraryBookRequest input,
        HttpRequest request,
        ClaimsPrincipal user,
        IDocumentStore store,
        OpenLibraryClient catalog,
        CancellationToken cancellationToken)
    {
        var editionId = input.EditionId.Trim();
        if (!LibraryBook.IsValidEditionId(editionId))
            return TypedResults.Problem("A valid Open Library edition ID is required.", statusCode: 400);

        var ownerKey = LibraryAuthentication.OwnerKey(user);
        var bookId = LibraryBook.DocumentId(editionId);
        var entryId = LibraryEntry.DocumentId(ownerKey, editionId);
        ValidatedCover? validated = null;
        EditionMetadata? metadata = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var session = store.OpenAsyncSession();
            var entry = await session.LoadAsync<LibraryEntry>(entryId, cancellationToken);
            var book = await session.LoadAsync<LibraryBook>(bookId, cancellationToken);
            if (entry is not null)
            {
                if (book is not null) return TypedResults.Ok(entry.ToGallery(book));
                break;
            }

            var snapshot = book is null ? catalog.FindSearchedEdition(editionId) : null;
            if (book is null && snapshot is null)
                return TypedResults.Problem("Search for this edition again, then add it.", statusCode: 404);
            if (snapshot is not null && metadata is null)
            {
                try { metadata = await catalog.GetEditionMetadataAsync(snapshot, cancellationToken); }
                catch (CatalogUnavailableException ex)
                {
                    return TypedResults.Problem(title: ex.Message, statusCode: 503);
                }
            }

            if (attempt == 0)
            {
                var files = request.Form.Files; // Form binding has already read and cached the form.
                if (files.Count > 1 || (files.Count == 1 && input.Cover is null))
                    return TypedResults.Problem("Supply at most one cover image.", statusCode: 400);
                if (input.Cover is not null)
                {
                    var result = await ValidateCoverAsync(input.Cover, cancellationToken);
                    if (!result.IsValid)
                        return TypedResults.Problem(result.Error, statusCode: result.StatusCode);
                    validated = result.Cover!;
                }
            }

            var now = DateTimeOffset.UtcNow;
            // One atomic batch. Empty change vectors make both writes create-only.
            if (book is null)
            {
                book = LibraryBook.From(snapshot!.Edition, snapshot.WorkId, metadata!, now);
                await session.StoreAsync(book, string.Empty, bookId, cancellationToken);
            }
            entry = new LibraryEntry { OwnerKey = ownerKey, BookId = bookId, AddedAt = now, HasLocalCover = validated is not null };
            await session.StoreAsync(entry, string.Empty, entryId, cancellationToken);
            using var coverBytes = validated is null ? null : new MemoryStream(validated.Bytes, writable: false);
            if (coverBytes is not null)
                session.Advanced.Attachments.Store(entryId, LibraryEntry.CoverAttachmentName, coverBytes, validated!.ContentType);

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return TypedResults.Json(entry.ToGallery(book), statusCode: StatusCodes.Status201Created);
            }
            catch (ConcurrencyException)
            {
                // Read the winning book/entry in a fresh session before retrying a missing write.
            }
        }
        return TypedResults.Problem("The library write conflicted and could not be read back.", statusCode: 409);
    }

    public static async Task<Results<FileStreamHttpResult, NotFound>> GetCover(
        string editionId, ClaimsPrincipal user, IDocumentStore store, CancellationToken cancellationToken)
    {
        if (!LibraryBook.IsValidEditionId(editionId)) return TypedResults.NotFound();
        var entryId = LibraryEntry.DocumentId(LibraryAuthentication.OwnerKey(user), editionId);
        var attachment = await store.Operations.SendAsync(new GetAttachmentOperation(
            entryId, LibraryEntry.CoverAttachmentName, AttachmentType.Document, null), token: cancellationToken);
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
        return CoverValidationResult.Valid(image, type);
    }

    private sealed record CoverValidationResult(ValidatedCover? Cover, string? Error, int StatusCode)
    {
        public bool IsValid => Cover is not null;
        public static CoverValidationResult Valid(byte[] bytes, string contentType) => new(new ValidatedCover(bytes, contentType), null, 200);
        public static CoverValidationResult Invalid(string error, int statusCode) => new(null, error, statusCode);
    }
    private sealed record ValidatedCover(byte[] Bytes, string ContentType);
}
