using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;

public static class ReadingHistoryHandler
{
    public static async Task<Ok<IReadOnlyList<ReadGalleryBook>>> GetReadBooks(ClaimsPrincipal user, IDocumentStore store, CancellationToken ct)
    {
        using var session = store.OpenAsyncSession();
        var ownerKey = LibraryAuthentication.OwnerKey(user);
        var entries = await session.Query<ReadingHistoryEntry>()
            .Customize(query => query.WaitForNonStaleResults(TimeSpan.FromSeconds(10)))
            .Where(entry => entry.OwnerKey == ownerKey)
            .ToListAsync(ct);
        if (entries.Count == 0) return TypedResults.Ok<IReadOnlyList<ReadGalleryBook>>([]);

        var editions = await session.LoadAsync<LibraryEdition>(entries.Select(entry => LibraryEdition.DocumentId(entry.EditionId)), ct);
        var works = await session.LoadAsync<LibraryBook>(entries.Select(entry => LibraryBook.DocumentId(entry.WorkId)).Distinct(), ct);
        var gallery = new List<ReadGalleryBook>(entries.Count);
        foreach (var entry in entries)
            if (editions[LibraryEdition.DocumentId(entry.EditionId)] is { } edition && works[LibraryBook.DocumentId(entry.WorkId)] is { } work)
                gallery.Add(entry.ToGallery(edition, work));
        return TypedResults.Ok<IReadOnlyList<ReadGalleryBook>>(gallery.OrderBy(book => book.Title, StringComparer.OrdinalIgnoreCase)
            .ThenBy(book => book.EditionId, StringComparer.Ordinal).ToArray());
    }

    public static async Task<Results<NoContent, ProblemHttpResult>> MarkAsRead(
        MarkBookAsReadRequest input, ClaimsPrincipal user, IDocumentStore store, OpenLibraryClient catalog, ILogger<LibraryBook> logger, CancellationToken ct)
    {
        if (input.Rating is < 1 or > 5 || input.Rating % 0.5m != 0)
            return TypedResults.Problem(detail: "Choose a rating from 1 to 5 stars in half-star increments.", statusCode: 400);

        var ownerKey = LibraryAuthentication.OwnerKey(user);
        try
        {
            await BookStore.SaveAsync(input.WorkId, input.EditionId, store, catalog, logger,
                editionId => ReadingHistoryEntry.DocumentId(ownerKey, editionId),
                book => new ReadingHistoryEntry { OwnerKey = ownerKey, WorkId = book.WorkId, EditionId = book.EditionId, Rating = input.Rating }, ct);
            return TypedResults.NoContent();
        }
        catch (BookWriteException ex) { return ex.ToProblem(); }
    }
}