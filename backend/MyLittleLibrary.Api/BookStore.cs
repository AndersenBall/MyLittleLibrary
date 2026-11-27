using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;
using Raven.Client.Exceptions;

internal static class BookStore
{
    // The user's entry and any missing catalog metadata commit together.
    public static async Task<(TEntry Entry, LibraryEdition Book, LibraryBook Work, bool Created)> SaveAsync<TEntry>(
        string? workId, string? editionId, IDocumentStore store, OpenLibraryClient catalog, ILogger logger,
        Func<string, string> entryDocumentId, Func<LibraryEdition, TEntry> createEntry, CancellationToken ct)
        where TEntry : class
    {
        workId = workId?.Trim();
        editionId = editionId?.Trim();
        if (workId is null || editionId is null || !LibraryBook.IsValidWorkId(workId) || !LibraryEdition.IsValidEditionId(editionId))
            throw new BookWriteException("Valid Open Library work and edition IDs are required.", 400);

        var bookId = LibraryEdition.DocumentId(editionId);
        var workDocumentId = LibraryBook.DocumentId(workId);
        var entryId = entryDocumentId(editionId);
        WorkImport? import = null;
        CatalogUnavailableException? importError = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var importedWork = false;
            using var session = store.OpenAsyncSession();
            var entry = await session.LoadAsync<TEntry>(entryId, ct);
            var work = await session.LoadAsync<LibraryBook>(workDocumentId, ct);
            var book = await session.LoadAsync<LibraryEdition>(bookId, ct);
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
                importedWork = true;
                book = LibraryEdition.From(selected, work.WorkId);
                await session.StoreAsync(work, string.Empty, workDocumentId, ct);
                foreach (var edition in import.Editions)
                {
                    var imported = edition.EditionId == editionId ? book : LibraryEdition.From(edition, work.WorkId);
                    await session.StoreAsync(imported, string.Empty, LibraryEdition.DocumentId(imported.EditionId), ct);
                }
            }

            entry = createEntry(book);
            await session.StoreAsync(entry, string.Empty, entryId, ct);
            try
            {
                await session.SaveChangesAsync(ct);
                if (importedWork) _ = Task.Run(() => EnrichGenresAsync(workId, store, catalog, logger));
                return (entry, book, work!, true);
            }
            catch (ConcurrencyException)
            {
                // Reload the winning documents; never overwrite existing catalog metadata or user entries.
            }
        }
        throw new BookWriteException("The book write conflicted and could not be read back.", 409);
    }

    private static async Task EnrichGenresAsync(string workId, IDocumentStore store, OpenLibraryClient catalog, ILogger logger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var genres = await catalog.FetchGenresAsync(workId, timeout.Token);
            if (genres is null) return;
            using var session = store.OpenAsyncSession();
            session.Advanced.Patch<LibraryBook, string[]?>(LibraryBook.DocumentId(workId), book => book.Genres, genres);
            await session.SaveChangesAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not enrich genres for {WorkId}", workId);
        }
    }
}

internal sealed class BookWriteException(string message, int statusCode) : Exception(message)
{
    public ProblemHttpResult ToProblem() => statusCode == 503
        ? TypedResults.Problem(title: Message, statusCode: statusCode)
        : TypedResults.Problem(detail: Message, statusCode: statusCode);
}