using System.Net;
using System.Net.Http.Json;
using Raven.Client.Documents.Operations;
using Xunit;

namespace MyLittleLibrary.IntegrationTests;

public sealed class GenreTimeoutTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    public async Task GenreTimeoutReturns503WithoutWriting(bool markRead, bool workTimeout, bool gatewayTimeout)
    {
        await using var api = new ApiScenario();
        api.Catalog.IncludeRedirectGenres = true;
        if (workTimeout) api.Catalog.TimeoutGenreWorkProvider = true;
        else
        {
            // The timeout occurs on the destination after following two redirects.
            api.Catalog.TimeoutGenreTagId = "OL176T";
            if (gatewayTimeout) api.Catalog.GenreTagTimeoutStatus = HttpStatusCode.GatewayTimeout;
        }

        var path = markRead ? "/api/reading-history/books" : "/api/library/books";
        using var response = await api.Client.PostAsJsonAsync(path,
            new { workId = "OL10000W", editionId = "OL100001M", rating = 5 }, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, (await api.Store.Maintenance.SendAsync(new GetStatisticsOperation(), TestContext.Current.CancellationToken)).CountOfDocuments);
        Assert.Contains(api.Catalog.Requests, uri => uri.AbsolutePath == (workTimeout ? "/works/OL10000W.json" : "/tags/OL176T.json"));
    }
}
