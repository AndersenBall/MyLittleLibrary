using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

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

        var added = await LoadAddedEditionsAsync(works.SelectMany(w => w.Editions), user, store, ct);
        return TypedResults.Ok(works.Select(w => w with { Editions = MarkAdded(w.Editions, added) }).ToArray());
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
        var added = await LoadAddedEditionsAsync(page.Editions, user, store, ct);
        return TypedResults.Ok(page with { Editions = MarkAdded(page.Editions, added) });
    }

    private static async Task<HashSet<string>> LoadAddedEditionsAsync(IEnumerable<CatalogEdition> editions,
        ClaimsPrincipal user, IDocumentStore store, CancellationToken ct)
    {
        var ids = editions.Select(e => e.EditionId).Distinct().ToArray();
        if (ids.Length == 0) return [];
        var ownerKey = LibraryAuthentication.OwnerKey(user);
        using var session = store.OpenAsyncSession();
        var saved = await session.LoadAsync<LibraryEntry>(ids.Select(id => LibraryEntry.DocumentId(ownerKey, id)), ct);
        return ids.Where(id => saved[LibraryEntry.DocumentId(ownerKey, id)] is not null).ToHashSet();
    }

    private static CatalogEdition[] MarkAdded(CatalogEdition[] editions, HashSet<string> added) =>
        editions.Select(e => added.Contains(e.EditionId) ? e with { AlreadyAdded = true } : e).ToArray();

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
        var workId = input.WorkId?.Trim();
        var editionId = input.EditionId?.Trim();
        if (workId is null || editionId is null || !LibraryWork.IsValidWorkId(workId) || !LibraryBook.IsValidEditionId(editionId))
            return TypedResults.Problem("Valid Open Library work and edition IDs are required.", statusCode: 400);

        var ownerKey = LibraryAuthentication.OwnerKey(user);
        var bookId = LibraryBook.DocumentId(editionId);
        var workDocumentId = LibraryWork.DocumentId(workId);
        var entryId = LibraryEntry.DocumentId(ownerKey, editionId);

        WorkImport? import = null;
        string? importError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var session = store.OpenAsyncSession();
            var entry = await session.LoadAsync<LibraryEntry>(entryId, cancellationToken);
            var work = await session.LoadAsync<LibraryWork>(workDocumentId, cancellationToken);
            var book = await session.LoadAsync<LibraryBook>(bookId, cancellationToken);
            if ((book is not null && book.WorkId != workId) || (work is not null && book is null))
                return TypedResults.Problem("This edition does not belong to the selected work.", statusCode: 404);
            if (book is not null && work is null) continue; // The import may have committed between reads.
            if (entry is not null)
            {
                if (book is not null && work is not null) return TypedResults.Ok(book.ToGallery(entry, work));
                break;
            }
            if (book is null && importError is not null)
                return TypedResults.Problem(title: importError, statusCode: 503);

            // One atomic batch. Empty change vectors make every write create-only.
            if (book is null)
            {
                try { import ??= await catalog.FetchWorkAsync(workId, cancellationToken); }
                catch (CatalogUnavailableException ex)
                {
                    // Another request may have imported the book while this provider request failed.
                    importError = ex.Message;
                    continue;
                }
                if (import is null) return TypedResults.Problem("Open Library could not find this work.", statusCode: 404);
                if (!import.Editions.Any(e => e.EditionId == editionId))
                    return TypedResults.Problem("This edition does not belong to the selected work.", statusCode: 404);
                work = import.Work;
                await session.StoreAsync(work, string.Empty, workDocumentId, cancellationToken);
                foreach (var edition in import.Editions)
                {
                    var imported = LibraryBook.From(edition, work.WorkId);
                    await session.StoreAsync(imported, string.Empty, LibraryBook.DocumentId(imported.EditionId), cancellationToken);
                    if (imported.EditionId == editionId) book = imported;
                }
            }
            entry = new LibraryEntry { OwnerKey = ownerKey, BookId = bookId, AddedAt = DateTimeOffset.UtcNow };
            await session.StoreAsync(entry, string.Empty, entryId, cancellationToken);

            try
            {
                await session.SaveChangesAsync(cancellationToken);
                return TypedResults.Json(book!.ToGallery(entry, work!), statusCode: StatusCodes.Status201Created);
            }
            catch (ConcurrencyException)
            {
                // Read the winning book/entry in a fresh session before retrying a missing write.
            }
        }
        return TypedResults.Problem("The library write conflicted and could not be read back.", statusCode: 409);
    }
}
