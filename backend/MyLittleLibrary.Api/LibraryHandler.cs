using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;

public static class LibraryHandler
{
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
