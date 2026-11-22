using System.ComponentModel.DataAnnotations;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;
using SkiaSharp;

public static class LibraryStorageExtensions
{
    public static IServiceCollection AddLibraryStorage(this IServiceCollection services, IConfiguration configuration)
    {
        var url = configuration["RavenDb:Url"] ?? "http://ravendb:8080";
        var database = configuration["RavenDb:Database"] ?? "MyLittleLibrary";
        services.AddSingleton<IDocumentStore>(_ => new DocumentStore { Urls = [url], Database = database }.Initialize());
        services.AddHostedService<RavenDatabaseInitializer>();
        return services;
    }

    public static IEndpointRouteBuilder MapLibraryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/library").WithTags("Library");
        group.MapGet("/books", GetBooksAsync)
            .WithName("getLibraryBooks")
            .Produces<IReadOnlyList<GalleryBook>>();
        group.MapPost("/books", AddBookAsync)
            .WithName("addLibraryBook")
            .Accepts<AddLibraryBookRequest>("multipart/form-data")
            .Produces<GalleryBook>(StatusCodes.Status200OK)
            .Produces<GalleryBook>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        group.MapGet("/books/{editionId}/cover", GetCoverAsync)
            .WithName("getLibraryCover")
            .Produces<FileResult>(StatusCodes.Status200OK, "image/jpeg", "image/png", "image/webp")
            .Produces(StatusCodes.Status404NotFound);
        return endpoints;
    }

    private static async Task<IResult> GetBooksAsync(IDocumentStore store, CancellationToken cancellationToken)
    {
        using var session = store.OpenAsyncSession();
        var books = await session.Query<LibraryBook>()
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .OrderByDescending(x => x.AddedAt)
            .ThenBy(x => x.EditionId)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<GalleryBook>>(books.Select(x => x.ToGallery()).ToArray());
    }

    private static async Task<IResult> AddBookAsync(
        HttpRequest request,
        IDocumentStore store,
        CatalogClient catalog,
        CancellationToken cancellationToken)
    {
        const long requestLimit = 5 * 1024 * 1024 + 64 * 1024;
        if (request.Headers["Sec-Fetch-Site"] == "cross-site")
            return TypedResults.Problem("Add books from the library application.", statusCode: 403);
        if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data;", StringComparison.OrdinalIgnoreCase))
            return TypedResults.Problem("Multipart form data is required.", statusCode: 415);
        if (request.ContentLength > requestLimit)
            return TypedResults.Problem("Cover must be 5 MiB or smaller.", statusCode: 413);
        if (request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
            size.MaxRequestBodySize = requestLimit;

        IFormCollection form;
        try { form = await request.ReadFormAsync(cancellationToken); }
        catch (BadHttpRequestException ex) { return TypedResults.Problem("Could not read the form.", statusCode: ex.StatusCode); }
        catch (InvalidDataException) { return TypedResults.Problem("Invalid multipart form.", statusCode: 400); }

        var editionId = form["editionId"].ToString().Trim();
        if (!LibraryBook.IsValidEditionId(editionId))
            return TypedResults.Problem("A valid Open Library edition ID is required.", statusCode: 400);

        var documentId = LibraryBook.DocumentId(editionId);
        using (var checkSession = store.OpenAsyncSession())
        {
            var existing = await checkSession.LoadAsync<LibraryBook>(documentId, cancellationToken);
            if (existing is not null)
                return TypedResults.Ok(existing.ToGallery());
        }

        var cover = form.Files.GetFile("cover");
        ValidatedCover? validated = null;
        if (form.Files.Count > 1 || (form.Files.Count == 1 && cover is null))
            return TypedResults.Problem("Supply at most one cover image.", statusCode: 400);
        if (cover is not null)
        {
            var result = await ValidateCoverAsync(cover, cancellationToken);
            if (!result.IsValid)
                return TypedResults.Problem(result.Error, statusCode: result.StatusCode);
            validated = result.Cover!;
        }

        using var coverBytes = validated?.Bytes;
        EditionSnapshot? snapshot;
        try { snapshot = await catalog.ResolveAsync(editionId, cancellationToken); }
        catch (CatalogUnavailableException)
        { return TypedResults.Problem("The catalog is temporarily unavailable.", statusCode: StatusCodes.Status503ServiceUnavailable); }
        if (snapshot is null)
            return TypedResults.Problem("Edition was not found in the catalog.", statusCode: 404);

        var edition = snapshot.Edition;
        var book = LibraryBook.From(edition, snapshot.WorkId, validated is not null);
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
            return TypedResults.Created($"/api/library/books/{Uri.EscapeDataString(editionId)}", book.ToGallery());
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

    private static async Task<IResult> GetCoverAsync(string editionId, IDocumentStore store, CancellationToken cancellationToken)
    {
        if (!LibraryBook.IsValidEditionId(editionId)) return TypedResults.NotFound();
        var session = store.OpenAsyncSession();
        var documentId = LibraryBook.DocumentId(editionId);
        var book = await session.LoadAsync<LibraryBook>(documentId, cancellationToken);
        if (book is null || !book.HasLocalCover)
        {
            session.Dispose();
            return TypedResults.NotFound();
        }
        var details = await session.Advanced.Attachments.GetAsync(documentId, LibraryBook.CoverAttachmentName, cancellationToken);
        if (details is null)
        {
            session.Dispose();
            return TypedResults.NotFound();
        }
        return new AttachmentResult(session, details.Stream, details.Details.ContentType);
    }

    private static async Task<CoverValidationResult> ValidateCoverAsync(IFormFile file, CancellationToken cancellationToken)
    {
        const long maxBytes = 5 * 1024 * 1024;
        if (file.Length > maxBytes) return CoverValidationResult.Invalid("Cover must be 5 MiB or smaller.", 413);
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

public sealed record GalleryBook(string EditionId, string Title, string[] Authors, string? CoverUrl, DateTimeOffset AddedAt);

public sealed class AddLibraryBookRequest
{
    [FromForm(Name = "editionId")]
    [Required]
    public string EditionId { get; set; } = "";

    [FromForm(Name = "cover")]
    public IFormFile? Cover { get; set; }
}

public sealed class LibraryBook
{
    public const string CoverAttachmentName = "cover";
    public string EditionId { get; set; } = "";
    public string? WorkId { get; set; }
    public string Title { get; set; } = "";
    public string[] Authors { get; set; } = Array.Empty<string>();
    public string[] Publishers { get; set; } = Array.Empty<string>();
    public string? PublicationDate { get; set; }
    public string[] Isbn10 { get; set; } = Array.Empty<string>();
    public string[] Isbn13 { get; set; } = Array.Empty<string>();
    public string? CoverUrl { get; set; }
    public string EditionUrl { get; set; } = "";
    public DateTimeOffset AddedAt { get; set; }
    public bool HasLocalCover { get; set; }

    public static string DocumentId(string editionId) => "LibraryBooks/" + editionId;
    public static bool IsValidEditionId(string editionId) => Regex.IsMatch(editionId, "\\AOL[0-9]{1,15}M\\z", RegexOptions.CultureInvariant);
    public static LibraryBook From(CatalogEdition edition, string? workId, bool hasLocalCover) => new()
    {
        EditionId = edition.EditionId, WorkId = workId, Title = edition.Title,
        Authors = edition.Authors ?? Array.Empty<string>(), Publishers = edition.Publishers ?? Array.Empty<string>(),
        PublicationDate = edition.PublicationDate, Isbn10 = edition.Isbn10 ?? Array.Empty<string>(),
        Isbn13 = edition.Isbn13 ?? Array.Empty<string>(), CoverUrl = edition.CoverUrl,
        EditionUrl = edition.EditionUrl, AddedAt = DateTimeOffset.UtcNow, HasLocalCover = hasLocalCover
    };
    public GalleryBook ToGallery() => new(EditionId, Title, Authors, HasLocalCover ? $"/api/library/books/{Uri.EscapeDataString(EditionId)}/cover" : CoverUrl, AddedAt);
}


public sealed class AttachmentResult(IAsyncDocumentSession session, Stream stream, string? contentType) : IResult
{
    public async Task ExecuteAsync(HttpContext httpContext)
    {
        httpContext.Response.ContentType = contentType ?? "application/octet-stream";
        try { await stream.CopyToAsync(httpContext.Response.Body, httpContext.RequestAborted); }
        finally { await stream.DisposeAsync(); session.Dispose(); }
    }
}

internal sealed class RavenDatabaseInitializer(IDocumentStore store) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await store.Maintenance.SendAsync(new GetStatisticsOperation(), cancellationToken);
                return;
            }
            catch (DatabaseDoesNotExistException)
            {
                try { await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(store.Database)), cancellationToken); }
                catch (ConcurrencyException) { /* Another instance is creating this database. */ }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt < 14 &&
                ex is HttpRequestException or TimeoutException or AllTopologyNodesDownException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
