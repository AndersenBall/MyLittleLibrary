using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Raven.Client.Documents.Operations;
using Xunit;

namespace MyLittleLibrary.IntegrationTests;

public sealed class LibraryFlowTests
{
    private const string BooksPath = "/api/library/books";
    private const string EditionId = "OL100001M";
    private const string IllustratedEditionId = "OL100002M";
    private const string Isbn = "9780547928227";
    private const string CatalogIsbnQuery = "isbn:" + Isbn;
    private const string TitleQuery = "The Hobbit";
    private const string Author = "J. R. R. Tolkien";
    // A 1×1 RGB PNG. Keep the original bytes for the round-trip assertions.
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQLd0AAAHdATsQfYP1AAAAAElFTkSuQmCC");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IsbnSearchReturnsBothEditionsAndMarksTheSavedEditionOnRepeatSearch()
    {
        await using var api = new ApiScenario(CatalogIsbnQuery, CatalogIsbnQuery);
        Assert.Empty(await ShelfAsync(api.Client));

        var editions = await SearchAsync(api.Client, "978-0-547-92822-7");
        Assert.Equal(2, editions.Length);
        var match = Assert.Single(editions, edition => edition.EditionId == EditionId);
        Assert.Equal(TitleQuery, match.Title);
        Assert.Equal(Isbn, Assert.Single(match.Isbn13));
        Assert.False(match.AlreadyAdded);
        var withoutIsbn = Assert.Single(editions, edition => edition.EditionId == IllustratedEditionId);
        Assert.Empty(withoutIsbn.Isbn10);
        Assert.Empty(withoutIsbn.Isbn13);

        using var form = BookForm(EditionId);
        using var response = await api.Client.PostAsync(BooksPath, form, Ct);
        var added = await ReadAsync<GalleryBook>(response, HttpStatusCode.Created);
        Assert.Equal(EditionId, added.EditionId);
        Assert.Equal(TitleQuery, added.Title);
        Assert.Equal(Author, Assert.Single(added.Authors));

        using var session = api.Store.OpenAsyncSession();
        var document = Assert.IsType<LibraryBook>(await session.LoadAsync<LibraryBook>("LibraryBooks/" + EditionId, Ct));
        Assert.Equal(EditionId, document.EditionId);
        Assert.Equal("OL10000W", document.WorkId);
        Assert.Equal(TitleQuery, document.Title);
        Assert.Equal(Isbn, Assert.Single(document.Isbn13));
        Assert.Equal(added.AddedAt, document.AddedAt);

        var saved = Assert.Single(await ShelfAsync(api.Client));
        Assert.Equal(EditionId, saved.EditionId);
        Assert.Equal(TitleQuery, saved.Title);
        Assert.Equal(Author, Assert.Single(saved.Authors));
        Assert.Equal(added.AddedAt, saved.AddedAt);

        var repeated = await SearchAsync(api.Client, "978-0-547-92822-7");
        Assert.True(Assert.Single(repeated, edition => edition.EditionId == EditionId).AlreadyAdded);
        Assert.False(Assert.Single(repeated, edition => edition.EditionId == IllustratedEditionId).AlreadyAdded);
        Assert.Equal(2, api.Catalog.Requests.Count);
    }

    [Fact]
    public async Task AddRequiresARecentSearchForAnUnsavedEdition()
    {
        await using var api = new ApiScenario(TitleQuery);
        using var beforeSearch = BookForm(EditionId);
        using var rejected = await api.Client.PostAsync(BooksPath, beforeSearch, Ct);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        Assert.Equal(0, await DocumentCountAsync(api));
        Assert.Empty(api.Catalog.Requests);

        await SearchAsync(api.Client, TitleQuery);
        using var afterSearch = BookForm(EditionId);
        using var accepted = await api.Client.PostAsync(BooksPath, afterSearch, Ct);
        var added = await ReadAsync<GalleryBook>(accepted, HttpStatusCode.Created);
        Assert.Equal(EditionId, added.EditionId);
        Assert.Equal(1, await DocumentCountAsync(api));
        Assert.Single(api.Catalog.Requests);
    }

    [Fact]
    public async Task ConcurrentAddsReturnTheSameBookAndSaveOneDocument()
    {
        await using var api = new ApiScenario(TitleQuery);
        await SearchAsync(api.Client, TitleQuery);
        using var firstForm = BookForm(EditionId);
        using var secondForm = BookForm(EditionId);
        var responses = await Task.WhenAll(
            api.Client.PostAsync(BooksPath, firstForm, Ct),
            api.Client.PostAsync(BooksPath, secondForm, Ct));
        using var first = responses[0];
        using var second = responses[1];

        var statuses = responses.Select(response => response.StatusCode).Order().ToArray();
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], statuses);
        var added = await ReadAsync<GalleryBook>(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
        var duplicate = await ReadAsync<GalleryBook>(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK);
        Assert.Equal(EditionId, added.EditionId);
        Assert.Equal(EditionId, duplicate.EditionId);
        Assert.Equal(added.AddedAt, duplicate.AddedAt);
        Assert.Equal(1, await DocumentCountAsync(api));
        Assert.Single(api.Catalog.Requests);
    }

    [Fact]
    public async Task CoverRoundTripsAsPngAndCorruptCoverDoesNotSaveABook()
    {
        await using var api = new ApiScenario(TitleQuery);
        await SearchAsync(api.Client, TitleQuery);
        using var valid = BookForm(EditionId, Png);
        using var accepted = await api.Client.PostAsync(BooksPath, valid, Ct);
        var added = await ReadAsync<GalleryBook>(accepted, HttpStatusCode.Created);
        await AssertCoverAsync(api.Client, added);

        using var invalid = BookForm(IllustratedEditionId, "not a PNG"u8.ToArray());
        using var rejected = await api.Client.PostAsync(BooksPath, invalid, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var session = api.Store.OpenAsyncSession();
        Assert.Null(await session.LoadAsync<LibraryBook>("LibraryBooks/" + IllustratedEditionId, Ct));
        Assert.Equal(1, await DocumentCountAsync(api));
        Assert.Single(api.Catalog.Requests);
    }

    [Fact]
    public async Task CatalogOutageDoesNotPreventShelfCoverOrRepeatAdd()
    {
        await using var api = new ApiScenario(TitleQuery, TitleQuery);
        await SearchAsync(api.Client, TitleQuery);
        using var form = BookForm(EditionId, Png);
        using var accepted = await api.Client.PostAsync(BooksPath, form, Ct);
        var added = await ReadAsync<GalleryBook>(accepted, HttpStatusCode.Created);

        api.Catalog.FailSearches = true;
        using var unavailable = await api.Client.GetAsync(SearchPath(TitleQuery), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var callsAfterOutage = api.Catalog.Requests.Count;
        Assert.Equal(2, callsAfterOutage);

        var saved = Assert.Single(await ShelfAsync(api.Client));
        Assert.Equal(EditionId, saved.EditionId);
        Assert.Equal(added.AddedAt, saved.AddedAt);
        Assert.Equal(added.CoverUrl, saved.CoverUrl);
        await AssertCoverAsync(api.Client, saved);

        using var repeat = BookForm(EditionId);
        using var response = await api.Client.PostAsync(BooksPath, repeat, Ct);
        var duplicate = await ReadAsync<GalleryBook>(response, HttpStatusCode.OK);
        Assert.Equal(EditionId, duplicate.EditionId);
        Assert.Equal(added.AddedAt, duplicate.AddedAt);
        Assert.Equal(added.CoverUrl, duplicate.CoverUrl);
        Assert.Equal(callsAfterOutage, api.Catalog.Requests.Count);
    }

    private static string SearchPath(string query) => $"/api/catalog/search?q={Uri.EscapeDataString(query)}";

    private static async Task<CatalogEdition[]> SearchAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(SearchPath(query), Ct);
        return await ReadAsync<CatalogEdition[]>(response, HttpStatusCode.OK);
    }

    private static async Task<GalleryBook[]> ShelfAsync(HttpClient client)
    {
        using var response = await client.GetAsync(BooksPath, Ct);
        return await ReadAsync<GalleryBook[]>(response, HttpStatusCode.OK);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(Ct));
    }

    private static async Task<long> DocumentCountAsync(ApiScenario api) =>
        (await api.Store.Maintenance.SendAsync(new GetStatisticsOperation(), Ct)).CountOfDocuments;

    private static MultipartFormDataContent BookForm(string editionId, byte[]? cover = null)
    {
        var form = new MultipartFormDataContent { { new StringContent(editionId), "editionId" } };
        if (cover is not null)
        {
            var file = new ByteArrayContent(cover);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "cover", "cover.png");
        }
        return form;
    }

    private static async Task AssertCoverAsync(HttpClient client, GalleryBook book)
    {
        Assert.Equal($"{BooksPath}/{book.EditionId}/cover", book.CoverUrl);
        using var response = await client.GetAsync(book.CoverUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync(Ct));
    }
}
