using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;
using Raven.Client.Exceptions;

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
        var reviewComment = input.ReviewComment?.Trim();
        if (ValidateReview(input.Rating, reviewComment) is { } error) return error;

        var ownerKey = LibraryAuthentication.OwnerKey(user);
        try
        {
            await BookStore.SaveAsync(input.WorkId, input.EditionId, store, catalog, logger,
                editionId => ReadingHistoryEntry.DocumentId(ownerKey, editionId),
                book => new ReadingHistoryEntry
                {
                    OwnerKey = ownerKey, WorkId = book.WorkId, EditionId = book.EditionId, Rating = input.Rating,
                    ReviewComment = string.IsNullOrEmpty(reviewComment) ? null : reviewComment
                }, ct);
            return TypedResults.NoContent();
        }
        catch (BookWriteException ex) { return ex.ToProblem(); }
    }

    public static async Task<Results<NoContent, NotFound>> RemoveReadBook(
        string editionId, ClaimsPrincipal user, IDocumentStore store, CancellationToken ct)
    {
        if (!LibraryEdition.IsValidEditionId(editionId)) return TypedResults.NotFound();

        using var session = store.OpenAsyncSession();
        session.Delete(ReadingHistoryEntry.DocumentId(LibraryAuthentication.OwnerKey(user), editionId));
        await session.SaveChangesAsync(ct);
        return TypedResults.NoContent();
    }

    public static async Task<Results<NoContent, NotFound, ProblemHttpResult>> UpdateReview(
        string editionId, UpdateBookReviewRequest input, ClaimsPrincipal user, IDocumentStore store, CancellationToken ct)
    {
        if (!LibraryEdition.IsValidEditionId(editionId)) return TypedResults.NotFound();
        var reviewComment = input.ReviewComment?.Trim();
        if (ValidateReview(input.Rating, reviewComment) is { } error) return error;

        using var session = store.OpenAsyncSession();
        session.Advanced.OptimisticConcurrencyMode = OptimisticConcurrencyMode.Writes;
        var entry = await session.LoadAsync<ReadingHistoryEntry>(
            ReadingHistoryEntry.DocumentId(LibraryAuthentication.OwnerKey(user), editionId), ct);
        if (entry is null) return TypedResults.NotFound();

        entry.Rating = input.Rating;
        entry.ReviewComment = string.IsNullOrEmpty(reviewComment) ? null : reviewComment;
        try { await session.SaveChangesAsync(ct); }
        catch (ConcurrencyException)
        {
            return TypedResults.Problem(detail: "This read entry changed. Reopen the book to see the latest review.", statusCode: 409);
        }
        return TypedResults.NoContent();
    }

    private static ProblemHttpResult? ValidateReview(decimal rating, string? reviewComment)
    {
        if (rating is < 1 or > 5 || rating % 0.5m != 0)
            return TypedResults.Problem(detail: "Choose a rating from 1 to 5 stars in half-star increments.", statusCode: 400);
        if (reviewComment?.Length > 2000)
            return TypedResults.Problem(detail: "Keep your review comment to 2,000 characters or fewer.", statusCode: 400);
        return null;
    }
}