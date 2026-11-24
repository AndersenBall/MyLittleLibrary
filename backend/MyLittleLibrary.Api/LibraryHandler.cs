using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;

public static class LibraryHandler
{
    public static async Task<Results<Ok<CatalogWork[]>, ValidationProblem, ProblemHttpResult>> SearchCatalog(
        string? q, ClaimsPrincipal user, OpenLibraryClient catalog, IDocumentStore store, CancellationToken ct)
    {
        var query = q?.Trim();
        if (string.IsNullOrEmpty(query) || query.Length > 200)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Enter a title, author, or ISBN (up to 200 characters)."] });

        CatalogWork[] works;
        try { works = await catalog.SearchAsync(query, ct); }
        catch (CatalogUnavailableException ex)
        {
            return TypedResults.Problem(title: ex.Message, statusCode: 503);
        }

        var states = await LoadEditionStatesAsync(works.SelectMany(w => w.Editions), user, store, ct);
        return TypedResults.Ok(works.Select(w => w with { Editions = MarkEditionStates(w.Editions, states) }).ToArray());
    }

    public static async Task<Results<Ok<CatalogEditionPage>, ProblemHttpResult>> GetCatalogEditions(
        string workId, int? offset, ClaimsPrincipal user, OpenLibraryClient catalog, IDocumentStore store, CancellationToken ct)
    {
        if (offset < 0) return TypedResults.Problem("The edition offset must not be negative.", statusCode: 400);
        if (!LibraryWork.IsValidWorkId(workId))
            return TypedResults.Problem("A valid Open Library work ID is required.", statusCode: 400);

        CatalogEditionPage? page;
        try { page = await catalog.BrowseEditionPageAsync(workId, offset ?? 0, ct); }
        catch (CatalogUnavailableException ex)
        {
            return TypedResults.Problem(title: ex.Message, statusCode: 503);
        }
        if (page is null) return TypedResults.Problem("Open Library could not find this work.", statusCode: 404);
        var states = await LoadEditionStatesAsync(page.Editions, user, store, ct);
        return TypedResults.Ok(page with { Editions = MarkEditionStates(page.Editions, states) });
    }

    private static async Task<Dictionary<string, (bool Added, bool Read)>> LoadEditionStatesAsync(IEnumerable<CatalogEdition> editions,
        ClaimsPrincipal user, IDocumentStore store, CancellationToken ct)
    {
        var ids = editions.Select(e => e.EditionId).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var ownerKey = LibraryAuthentication.OwnerKey(user);
        using var session = store.OpenAsyncSession();
        var saved = await session.LoadAsync<LibraryEntry>(ids.Select(id => LibraryEntry.DocumentId(ownerKey, id)), ct);
        var read = await ReadingHistoryHandler.LoadReadEditionsAsync(ids, ownerKey, session, ct);
        return ids.ToDictionary(id => id, id => (saved[LibraryEntry.DocumentId(ownerKey, id)] is not null, read.Contains(id)));
    }

    private static CatalogEdition[] MarkEditionStates(CatalogEdition[] editions, Dictionary<string, (bool Added, bool Read)> states) =>
        editions.Select(e => e with { AlreadyAdded = states[e.EditionId].Added, AlreadyRead = states[e.EditionId].Read }).ToArray();

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
        var works = await session.LoadAsync<LibraryWork>(books.Values.Where(b => b is not null)
            .Select(b => LibraryWork.DocumentId(b.WorkId)), cancellationToken);
        var gallery = new List<GalleryBook>(entries.Count);
        foreach (var entry in entries)
            if (books[entry.BookId] is { } book && works[LibraryWork.DocumentId(book.WorkId)] is { } work)
                gallery.Add(book.ToGallery(entry, work));
        return TypedResults.Ok<IReadOnlyList<GalleryBook>>(gallery);
    }

    public static async Task<Results<Ok<GalleryBook>, JsonHttpResult<GalleryBook>, ProblemHttpResult>> AddBook(
        AddLibraryBookRequest input,
        ClaimsPrincipal user,
        IDocumentStore store,
        OpenLibraryClient catalog,
        CancellationToken cancellationToken)
    {
        var ownerKey = LibraryAuthentication.OwnerKey(user);
        try
        {
            var (entry, book, work, created) = await BookStore.SaveAsync(input.WorkId, input.EditionId, store, catalog,
                editionId => LibraryEntry.DocumentId(ownerKey, editionId),
                book => new LibraryEntry { OwnerKey = ownerKey, BookId = LibraryBook.DocumentId(book.EditionId), AddedAt = DateTimeOffset.UtcNow },
                cancellationToken);
            var result = book.ToGallery(entry, work);
            if (created) return TypedResults.Json(result, statusCode: StatusCodes.Status201Created);
            return TypedResults.Ok(result);
        }
        catch (BookWriteException ex) { return ex.ToProblem(); }
    }
}
