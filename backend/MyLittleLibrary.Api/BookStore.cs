using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

internal static class BookStore
{
    // The user's entry and any missing catalog metadata commit together.
    public static async Task<(TEntry Entry, LibraryBook Book, LibraryWork Work, bool Created)> SaveAsync<TEntry>(
        string? workId, string? editionId, IDocumentStore store, OpenLibraryClient catalog,
        Func<string, string> entryDocumentId, Func<LibraryBook, TEntry> createEntry, CancellationToken ct)
        where TEntry : class
    {
        workId = workId?.Trim();
        editionId = editionId?.Trim();
        if (workId is null || editionId is null || !LibraryWork.IsValidWorkId(workId) || !LibraryBook.IsValidEditionId(editionId))
            throw new BookWriteException("Valid Open Library work and edition IDs are required.", 400);

        var bookId = LibraryBook.DocumentId(editionId);
        var workDocumentId = LibraryWork.DocumentId(workId);
        var entryId = entryDocumentId(editionId);
        WorkImport? import = null;
        CatalogUnavailableException? importError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var session = store.OpenAsyncSession();
            var entry = await session.LoadAsync<TEntry>(entryId, ct);
            var work = await session.LoadAsync<LibraryWork>(workDocumentId, ct);
            var book = await session.LoadAsync<LibraryBook>(bookId, ct);
            if ((book is not null && book.WorkId != workId) || (work is not null && book is null))
                throw new BookWriteException("This edition does not belong to the selected work.", 404);
            if (book is not null && work is null) continue; // The import may have committed between reads.

            if (entry is not null)
            {
                if (book is not null && work is not null) return (entry, book, work, false);
                break;
            }

            if (book is null)
            {
                if (importError is not null) throw new BookWriteException(importError.Message, 503);
                try { import ??= await catalog.FetchWorkAsync(workId, ct); }
                catch (CatalogUnavailableException ex)
                {
                    // Another request may have completed the import while the provider request failed.
                    importError = ex;
                    continue;
                }
                if (import is null) throw new BookWriteException("Open Library could not find this work.", 404);
                var selected = import.Editions.FirstOrDefault(e => e.EditionId == editionId)
                    ?? throw new BookWriteException("This edition does not belong to the selected work.", 404);
                work = import.Work;
                book = LibraryBook.From(selected, work.WorkId);
                await session.StoreAsync(work, string.Empty, workDocumentId, ct);
                foreach (var edition in import.Editions)
                {
                    var imported = edition.EditionId == editionId ? book : LibraryBook.From(edition, work.WorkId);
                    await session.StoreAsync(imported, string.Empty, LibraryBook.DocumentId(imported.EditionId), ct);
                }
            }

            entry = createEntry(book);
            await session.StoreAsync(entry, string.Empty, entryId, ct);
            try
            {
                await session.SaveChangesAsync(ct);
                return (entry, book, work!, true);
            }
            catch (ConcurrencyException)
            {
                // Reload the winning documents; never overwrite existing catalog metadata or user entries.
            }
        }
        throw new BookWriteException("The book write conflicted and could not be read back.", 409);
    }
}

internal sealed class BookWriteException(string message, int statusCode) : Exception(message)
{
    public ProblemHttpResult ToProblem() => statusCode == 503
        ? TypedResults.Problem(title: Message, statusCode: statusCode)
        : TypedResults.Problem(detail: Message, statusCode: statusCode);
}