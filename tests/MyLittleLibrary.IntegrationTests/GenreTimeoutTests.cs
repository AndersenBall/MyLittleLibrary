using System.Net;
using System.Net.Http.Json;
using Raven.Client.Documents.Operations;
using Xunit;

namespace MyLittleLibrary.IntegrationTests;

public sealed class GenreTimeoutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteCommitsBeforeBlockedGenreLookupAndReturnsVisibleEntry(bool markRead)
    {
        await using var api = new ApiScenario();
        api.Catalog.IncludeGenres = true;
        api.Catalog.BlockGenreLookup = true;

        using var response = await PostAsync(api, markRead, Ct);
        Assert.Equal(markRead ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(4, await DocumentCountAsync(api));
        if (!markRead) Assert.Equal(EditionId, Assert.Single(await ShelfAsync(api)).EditionId);

        await api.Catalog.GenreLookupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        using (var session = api.Store.OpenAsyncSession())
        {
            var work = await session.LoadAsync<LibraryBook>(LibraryBook.DocumentId(WorkId), TestContext.Current.CancellationToken);
            Assert.NotNull(work);
            Assert.Null(work!.Genres);
        }

        using (var session = api.Store.OpenAsyncSession())
        {
            session.Advanced.Patch<LibraryBook, string>(LibraryBook.DocumentId(WorkId), work => work.Title, "Updated title");
            await session.SaveChangesAsync(Ct);
        }
        api.Catalog.ReleaseGenreLookup.TrySetResult();
        await WaitForGenresAsync(api, ["Fantasy", "Literary"]);
        using var readback = api.Store.OpenAsyncSession();
        Assert.Equal("Updated title", (await readback.LoadAsync<LibraryBook>(LibraryBook.DocumentId(WorkId), Ct))!.Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledWriteDoesNotCancelGenreResolution(bool markRead)
    {
        await using var api = new ApiScenario();
        api.Catalog.IncludeGenres = true;
        api.Catalog.BlockGenreLookup = true;
        using var requestCancellation = new CancellationTokenSource();
        using var response = await PostAsync(api, markRead, requestCancellation.Token);
        Assert.Equal(markRead ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        requestCancellation.Cancel();

        await api.Catalog.GenreLookupStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        api.Catalog.ReleaseGenreLookup.TrySetResult();
        await WaitForGenresAsync(api, ["Fantasy", "Literary"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenreFailureLeavesCommittedWorkWithoutGenres(bool markRead)
    {
        await using var api = new ApiScenario();
        api.Catalog.IncludeGenres = true;
        api.Catalog.FailGenreWorkProvider = true;

        using var response = await PostAsync(api, markRead, Ct);
        Assert.Equal(markRead ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        await WaitForAsync(() => Task.FromResult(api.Catalog.Requests.Any(uri => uri.AbsolutePath == "/works/OL10000W.json")), expected: true);
        await WaitForAsync(async () =>
        {
            using var session = api.Store.OpenAsyncSession();
            var work = await session.LoadAsync<LibraryBook>(LibraryBook.DocumentId(WorkId), TestContext.Current.CancellationToken);
            return work is not null && work.Genres is null;
        }, expected: true);
        Assert.Equal(4, await DocumentCountAsync(api));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulGenreRedirectsResolveForBothWritePaths(bool markRead)
    {
        await using var api = new ApiScenario();
        api.Catalog.IncludeRedirectGenres = true;
        using var response = await PostAsync(api, markRead, Ct);
        Assert.Equal(markRead ? HttpStatusCode.NoContent : HttpStatusCode.Created, response.StatusCode);
        await WaitForGenresAsync(api, ["Fantasy", "Literary"]);
        Assert.Contains(api.Catalog.Requests, uri => uri.AbsolutePath == "/tags/OL176T.json");
        Assert.Contains(api.Catalog.Requests, uri => uri.AbsolutePath == "/tags/OL185T.json");
        Assert.DoesNotContain(api.Catalog.Requests, uri => uri.AbsolutePath == "/tags/OL186T.json");
    }

    private const string WorkId = "OL10000W";
    private const string EditionId = "OL100001M";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> PostAsync(ApiScenario api, bool markRead, CancellationToken cancellationToken) =>
        api.Client.PostAsJsonAsync(markRead ? "/api/reading-history/books" : "/api/library/books",
            new { workId = WorkId, editionId = EditionId, rating = 5 }, cancellationToken);

    private static async Task<GalleryBook[]> ShelfAsync(ApiScenario api)
    {
        using var response = await api.Client.GetAsync("/api/library/books", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.IsType<GalleryBook[]>(await response.Content.ReadFromJsonAsync<GalleryBook[]>(Ct));
    }

    private static async Task WaitForGenresAsync(ApiScenario api, string[] expected)
    {
        await WaitForAsync(async () =>
        {
            using var session = api.Store.OpenAsyncSession();
            var work = await session.LoadAsync<LibraryBook>(LibraryBook.DocumentId(WorkId), Ct);
            return work?.Genres?.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)) == true;
        }, expected: true);
    }

    private static async Task WaitForAsync(Func<Task<bool>> condition, bool expected)
    {
        for (var attempt = 0; attempt < 1500; attempt++)
        {
            if (await condition() == expected) return;
            await Task.Delay(20, Ct);
        }
        Assert.Fail("Timed out waiting for the asynchronous genre import.");
    }

    private static async Task<long> DocumentCountAsync(ApiScenario api) =>
        (await api.Store.Maintenance.SendAsync(new GetStatisticsOperation(), Ct)).CountOfDocuments;
}
