using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;

public static class ReadingHistoryHandler
{
    public static async Task<Results<NoContent, ProblemHttpResult>> MarkAsRead(
        MarkBookAsReadRequest input, ClaimsPrincipal user, IDocumentStore store, OpenLibraryClient catalog, CancellationToken ct)
    {
        var ownerKey = LibraryAuthentication.OwnerKey(user);
        try
        {
            await BookStore.SaveAsync(input.WorkId, input.EditionId, store, catalog,
                editionId => ReadingHistoryEntry.DocumentId(ownerKey, editionId),
                book => new ReadingHistoryEntry { OwnerKey = ownerKey, WorkId = book.WorkId, EditionId = book.EditionId }, ct);
            return TypedResults.NoContent();
        }
        catch (BookWriteException ex) { return ex.ToProblem(); }
    }
}