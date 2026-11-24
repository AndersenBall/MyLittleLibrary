using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;

public static class CatalogHandler
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
        var read = await session.LoadAsync<ReadingHistoryEntry>(ids.Select(id => ReadingHistoryEntry.DocumentId(ownerKey, id)), ct);
        return ids.ToDictionary(id => id, id => (saved[LibraryEntry.DocumentId(ownerKey, id)] is not null,
            read[ReadingHistoryEntry.DocumentId(ownerKey, id)] is not null));
    }

    private static CatalogEdition[] MarkEditionStates(CatalogEdition[] editions, Dictionary<string, (bool Added, bool Read)> states) =>
        editions.Select(e => e with { AlreadyAdded = states[e.EditionId].Added, AlreadyRead = states[e.EditionId].Read }).ToArray();
}