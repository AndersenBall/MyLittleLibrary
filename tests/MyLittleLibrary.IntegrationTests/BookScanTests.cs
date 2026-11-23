using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace MyLittleLibrary.IntegrationTests;

public sealed class BookScanTests
{
    private const string ScanPath = "/api/library/books/scan";
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQLd0AAAHdATsQfYP1AAAAAElFTkSuQmCC");
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ScanReturnsMultipleAndPartialResultsWithTrimmedValues()
    {
        await using var api = new ApiScenario();
        api.Scan.BooksJson = """
            {"books":[
              {"title":" Dune ","author":" Frank Herbert "},
              {"title":"The Hobbit","author":null},
              {"title":null,"author":" Ursula K. Le Guin "},
              {"title":"  ","author":null}
            ]}
            """;

        using var response = await api.Client.PostAsync(ScanPath, PhotoForm(Png), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = await response.Content.ReadFromJsonAsync<JsonDocument>(Ct);
        var books = json!.RootElement.GetProperty("books").EnumerateArray().ToArray();
        Assert.Equal(3, books.Length);
        Assert.Equal("Dune", books[0].GetProperty("title").GetString());
        Assert.Equal("Frank Herbert", books[0].GetProperty("author").GetString());
        Assert.Equal("The Hobbit", books[1].GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, books[1].GetProperty("author").ValueKind);
        Assert.Equal(JsonValueKind.Null, books[2].GetProperty("title").ValueKind);
        Assert.Equal("Ursula K. Le Guin", books[2].GetProperty("author").GetString());
        Assert.Equal(1, api.Scan.RequestCount);
    }

    [Fact]
    public async Task ScanReturnsEmptyArrayWhenNoBookTextIsReadable()
    {
        await using var api = new ApiScenario();
        using var response = await api.Client.PostAsync(ScanPath, PhotoForm(Png), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<ScanBooksResponse>(Ct);
        Assert.NotNull(result);
        Assert.Empty(result!.Books);
        Assert.Equal(1, api.Scan.RequestCount);
    }

    [Fact]
    public async Task ModelErrorReturnsUnavailable()
    {
        await using var api = new ApiScenario();
        api.Scan.StatusCode = HttpStatusCode.ServiceUnavailable;
        using var response = await api.Client.PostAsync(ScanPath, PhotoForm(Png), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var problem = await response.Content.ReadFromJsonAsync<JsonDocument>(Ct);
        Assert.Equal("Model unavailable", problem!.RootElement.GetProperty("title").GetString());
        Assert.Equal(1, api.Scan.RequestCount);
    }

    [Fact]
    public async Task AnonymousAndInvalidImageRequestsAreRejectedBeforeInference()
    {
        await using var api = new ApiScenario();
        using var anonymous = api.CreateClient(null);
        using var unauthenticated = await anonymous.PostAsync(ScanPath, PhotoForm(Png), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        Assert.Equal(0, api.Scan.RequestCount);

        using var invalid = await api.Client.PostAsync(ScanPath, PhotoForm("not an image"u8.ToArray()), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var problem = await invalid.Content.ReadFromJsonAsync<JsonDocument>(Ct);
        Assert.Equal("Invalid photo", problem!.RootElement.GetProperty("title").GetString());
        Assert.Equal(0, api.Scan.RequestCount);
    }

    [Fact]
    public async Task ConcurrentScanGetsRetryableBusyResponse()
    {
        await using var api = new ApiScenario();
        api.Scan.Delay = TimeSpan.FromMilliseconds(500);
        var firstRequest = api.Client.PostAsync(ScanPath, PhotoForm(Png), Ct);
        await api.Scan.RequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), Ct);
        using var busy = await api.Client.PostAsync(ScanPath, PhotoForm(Png), Ct);
        Assert.Equal((HttpStatusCode)429, busy.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(2), busy.Headers.RetryAfter?.Delta);
        using var completed = await firstRequest;
        Assert.Equal(HttpStatusCode.OK, completed.StatusCode);
        Assert.Equal(1, api.Scan.RequestCount);
    }

    private static MultipartFormDataContent PhotoForm(byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var photo = new ByteArrayContent(bytes);
        photo.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(photo, "photo", "photo.png");
        return form;
    }
}
