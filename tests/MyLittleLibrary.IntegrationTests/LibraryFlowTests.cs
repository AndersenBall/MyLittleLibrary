using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Raven.Client.Documents.Operations;
using Xunit;

namespace MyLittleLibrary.IntegrationTests;

public sealed class LibraryFlowTests
{
    private const string BooksPath = "/api/library/books";
    private const string WorkId = "OL10000W";
    private const string EditionsPath = $"/api/catalog/works/{WorkId}/editions";
    private const string EditionId = "OL100001M";
    private const string IllustratedId = "OL100002M";
    private const string PaperbackId = "OL100003M";
    private const string Isbn = "9780547928227";
    private const string Title = "The Hobbit";
    private const string Author = "J. R. R. Tolkien";
    private const string CatalogCoverUrl = "https://covers.openlibrary.org/b/id/1234-M.jpg?default=false";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task FirstAddFromASearchMatchImportsBothPagesAndThenReusesAnUnsearchedSibling()
    {
        await using var api = new ApiScenario(Title, $"isbn:{Isbn}");
        api.Catalog.EditionPages = [[EditionId, IllustratedId], [PaperbackId]];
        api.Catalog.IncludeGenres = true;
        var titleSearch = Assert.Single(await SearchAsync(api.Client, Title));
        Assert.All(titleSearch.Editions, e => Assert.Null(e.PageCount));
        Assert.Single(api.Catalog.Requests);

        var search = Assert.Single(await SearchAsync(api.Client, "978-0-547-92822-7"));
        Assert.Equal(WorkId, search.WorkId);
        Assert.Equal(Title, search.Title);
        Assert.Equal(Author, Assert.Single(search.Authors));
        Assert.Equal(1937, search.FirstPublicationYear);
        Assert.Equal([EditionId, IllustratedId], search.Editions.Select(e => e.EditionId));
        Assert.Equal(Isbn, Assert.Single(search.Editions[0].Isbn13));
        Assert.Equal(300, search.Editions[0].PageCount);
        Assert.Null(search.Editions[1].PageCount);
        Assert.All(search.Editions, e => Assert.False(e.AlreadyAdded));
        Assert.Equal(["/search.json", "/search.json", "/books/OL100001M.json"], api.Catalog.Requests.Select(uri => uri.AbsolutePath));
        Assert.Equal(0, await DocumentCountAsync(api));

        // This is the second nested match, not a browsed edition.
        var added = await AddAsync(api.Client, IllustratedId);
        var saved = Assert.Single(await ShelfAsync(api.Client));
        Assert.Equal(IllustratedId, saved.EditionId);
        Assert.Null(saved.CoverUrl);
        Assert.Equal(Author, Assert.Single(added.Authors));
        AssertImportRequests(api.Catalog.Requests.Skip(3), withGenres: true);
        Assert.Equal(5, await DocumentCountAsync(api));
        using (var session = api.Store.OpenAsyncSession())
        {
            var work = Assert.IsType<LibraryWork>(await session.LoadAsync<LibraryWork>($"LibraryWorks/{WorkId}", Ct));
            Assert.Equal(1938, work.FirstPublicationYear); // Work details override the search projection.
            Assert.Equal(Author, Assert.Single(work.Authors));
            Assert.Equal(["Adventure", "Dragons", "Fantasy"], work.Subjects.Order(StringComparer.Ordinal));
            Assert.Equal(["Fantasy", "Literary"], work.Genres.Order(StringComparer.Ordinal));
            Assert.Equal("Middle-earth", Assert.Single(work.Series));
            var editions = await session.Advanced.LoadStartingWithAsync<LibraryBook>("LibraryBooks/", token: Ct);
            Assert.Equal([EditionId, IllustratedId, PaperbackId], editions.Select(e => e.EditionId).Order(StringComparer.Ordinal));
            Assert.All(editions, e => Assert.Equal(WorkId, e.WorkId));
            var original = Assert.Single(editions, e => e.EditionId == EditionId);
            Assert.Equal("054792822X", Assert.Single(original.Isbn10));
            Assert.Equal(Isbn, Assert.Single(original.Isbn13));
            Assert.Equal("2012", original.PublicationDate);
            Assert.Equal(300, original.PageCount);
            var illustrated = Assert.Single(editions, e => e.EditionId == IllustratedId);
            Assert.Equal("May 2015", illustrated.PublicationDate);
            Assert.Equal(352, illustrated.PageCount);
            var sparse = Assert.Single(editions, e => e.EditionId == PaperbackId);
            Assert.Empty(sparse.Isbn10);
            Assert.Empty(sparse.Isbn13);
            Assert.Null(sparse.CoverUrl);
            Assert.Null(sparse.PublicationDate);
            Assert.Null(sparse.PageCount);
        }

        // The sibling was never returned to the browser.
        await AddAsync(api.Client, PaperbackId);
        Assert.Equal([IllustratedId, PaperbackId], (await ShelfAsync(api.Client)).Select(e => e.EditionId).Order(StringComparer.Ordinal));
        using var mismatch = await PostAsync(api.Client, PaperbackId, workId: "OL999W");
        Assert.Equal(HttpStatusCode.NotFound, mismatch.StatusCode);
        Assert.Equal(6, await DocumentCountAsync(api));
        Assert.Equal(9, api.Catalog.Requests.Count);
    }

    [Fact]
    public async Task BrowseRequestsOnePageAtATimeWithoutAPriorSearch()
    {
        await using var api = new ApiScenario();
        api.Catalog.EditionPages = [[EditionId, "OL100004M"], [PaperbackId, "OL100005M"], [IllustratedId]];

        using var firstResponse = await api.Client.GetAsync(EditionsPath, Ct);
        var first = await ReadAsync<CatalogEditionPage>(firstResponse, HttpStatusCode.OK);
        Assert.Equal(EditionId, Assert.Single(first.Editions).EditionId);
        Assert.Equal(300, first.Editions[0].PageCount);
        Assert.False(first.Editions[0].AlreadyAdded);
        Assert.Equal(2, first.NextOffset);
        Assert.Equal("?limit=25&offset=0", api.Catalog.Requests.Last().Query);
        Assert.Single(api.Catalog.Requests);
        using var secondResponse = await api.Client.GetAsync($"{EditionsPath}?offset={first.NextOffset}", Ct);
        var second = await ReadAsync<CatalogEditionPage>(secondResponse, HttpStatusCode.OK);
        Assert.Empty(second.Editions);
        Assert.Equal(4, second.NextOffset);
        Assert.Equal("?limit=25&offset=2", api.Catalog.Requests.Last().Query);
        using var thirdResponse = await api.Client.GetAsync($"{EditionsPath}?offset={second.NextOffset}", Ct);
        var third = await ReadAsync<CatalogEditionPage>(thirdResponse, HttpStatusCode.OK);
        Assert.Equal(IllustratedId, Assert.Single(third.Editions).EditionId);
        Assert.Equal(352, third.Editions[0].PageCount);
        Assert.Null(third.NextOffset);
        Assert.Equal("?limit=25&offset=4", api.Catalog.Requests.Last().Query);
        Assert.Equal(3, api.Catalog.Requests.Count);
        Assert.Equal(0, await DocumentCountAsync(api));

        await AddAsync(api.Client, EditionId);
        Assert.Collection(api.Catalog.Requests.Skip(3),
            uri => Assert.Equal("/works/OL10000W.json", uri.PathAndQuery),
            uri => Assert.Equal("/search.json", uri.AbsolutePath),
            uri => Assert.Equal("/works/OL10000W/editions.json?limit=100&offset=0", uri.PathAndQuery),
            uri => Assert.Equal("/works/OL10000W/editions.json?limit=100&offset=2", uri.PathAndQuery),
            uri => Assert.Equal("/works/OL10000W/editions.json?limit=100&offset=4", uri.PathAndQuery));
        Assert.Equal(EditionId, Assert.Single(await ShelfAsync(api.Client)).EditionId);
        using (var session = api.Store.OpenAsyncSession())
        {
            var imported = await session.Advanced.LoadStartingWithAsync<LibraryBook>("LibraryBooks/", token: Ct);
            Assert.Equal(5, imported.Count());
            Assert.Contains(imported, e => e.EditionId == PaperbackId);
            Assert.Contains(imported, e => e.EditionId == "OL100004M");
        }
        using var addedResponse = await api.Client.GetAsync(EditionsPath, Ct);
        var addedPage = await ReadAsync<CatalogEditionPage>(addedResponse, HttpStatusCode.OK);
        Assert.True(Assert.Single(addedPage.Editions).AlreadyAdded);
        using var other = api.CreateClient("another-reader");
        using var otherResponse = await other.GetAsync(EditionsPath, Ct);
        var otherPage = await ReadAsync<CatalogEditionPage>(otherResponse, HttpStatusCode.OK);
        Assert.False(Assert.Single(otherPage.Editions).AlreadyAdded);
    }

    [Fact]
    public async Task MissingWorksReturnNotFoundAndInvalidRequestsDoNotReachTheProvider()
    {
        await using var api = new ApiScenario();
        using var invalidWork = await PostAsync(api.Client, EditionId, workId: "invalid");
        Assert.Equal(HttpStatusCode.BadRequest, invalidWork.StatusCode);
        using var missingEdition = await api.Client.PostAsJsonAsync(BooksPath, new { workId = WorkId }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, missingEdition.StatusCode);
        using var nullEdition = await api.Client.PostAsJsonAsync(BooksPath, new { workId = WorkId, editionId = (string?)null }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, nullEdition.StatusCode);
        using var invalidOffset = await api.Client.GetAsync($"{EditionsPath}?offset=-1", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, invalidOffset.StatusCode);
        Assert.Empty(api.Catalog.Requests);
        using var missingWork = await PostAsync(api.Client, EditionId, workId: "OL999W");
        Assert.Equal(HttpStatusCode.NotFound, missingWork.StatusCode);
        using var missingEditions = await api.Client.GetAsync("/api/catalog/works/OL999W/editions", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missingEditions.StatusCode);
        Assert.Equal(2, api.Catalog.Requests.Count);
        Assert.Equal(0, await DocumentCountAsync(api));
    }

    [Fact]
    public async Task FailedEditionPageLeavesNoWritesAndCanBeRetried()
    {
        await using var api = new ApiScenario();
        api.Catalog.EditionPages = [[EditionId, IllustratedId], [PaperbackId]];
        api.Catalog.FailEditionOffset = 2;
        using var failed = await PostAsync(api.Client, EditionId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("?limit=100&offset=2", api.Catalog.Requests.Last().Query);
        Assert.Equal(0, await DocumentCountAsync(api));
        Assert.Empty(await ShelfAsync(api.Client));

        api.Catalog.FailEditionOffset = null;
        var added = await AddAsync(api.Client, EditionId);
        AssertImportRequests(api.Catalog.Requests.Skip(4));
        Assert.Equal(5, await DocumentCountAsync(api));
        Assert.Equal(CatalogCoverUrl, added.CoverUrl);
        Assert.Equal(CatalogCoverUrl, Assert.Single(await ShelfAsync(api.Client)).CoverUrl);
    }

    [Fact]
    public async Task FailedGenreLookupLeavesNoWrites()
    {
        await using var api = new ApiScenario();
        api.Catalog.IncludeGenres = true;
        api.Catalog.FailGenreLookup = true;

        using var failed = await PostAsync(api.Client, EditionId);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal("/tags/OL174T.json", api.Catalog.Requests.Last().PathAndQuery);
        Assert.Equal(0, await DocumentCountAsync(api));
    }

    [Fact]
    public async Task SuccessfulEditionListThatOmitsTheSelectedMatchLeavesNoWrites()
    {
        await using var api = new ApiScenario(Title);
        api.Catalog.EditionPages = [[IllustratedId]];
        await SearchAsync(api.Client, Title);
        using var rejected = await PostAsync(api.Client, EditionId);
        Assert.Equal(HttpStatusCode.NotFound, rejected.StatusCode);
        Assert.Equal(0, await DocumentCountAsync(api));
        Assert.Equal(4, api.Catalog.Requests.Count);
    }

    [Fact]
    public async Task ConcurrentAddsByTheSameOwnerReturnOneEntry()
    {
        await using var api = new ApiScenario(Title);
        await SearchAsync(api.Client, Title);
        var responses = await Task.WhenAll(PostAsync(api.Client, EditionId), PostAsync(api.Client, EditionId));
        using var first = responses[0];
        using var second = responses[1];
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Created], responses.Select(r => r.StatusCode).Order());
        var created = await ReadAsync<GalleryBook>(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created), HttpStatusCode.Created);
        var repeated = await ReadAsync<GalleryBook>(Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK), HttpStatusCode.OK);
        Assert.Equal(created.EditionId, repeated.EditionId);
        Assert.Equal(created.AddedAt, repeated.AddedAt);
        Assert.Equal(4, await DocumentCountAsync(api));
        Assert.Equal(EditionId, Assert.Single(await ShelfAsync(api.Client)).EditionId);
    }

    [Fact]
    public async Task OwnersShareImportedMetadataButNotShelfState()
    {
        await using var api = new ApiScenario(Title, Title, Title);
        using var other = api.CreateClient("reader"); // Differs from the first owner's claim only by case.
        await SearchAsync(api.Client, Title);
        await AddAsync(api.Client, EditionId);
        await AddAsync(other, EditionId);
        await AddAsync(api.Client, IllustratedId);
        Assert.Equal(6, await DocumentCountAsync(api));
        Assert.All(Assert.Single(await SearchAsync(api.Client, Title)).Editions, e => Assert.True(e.AlreadyAdded));
        Assert.All(Assert.Single(await SearchAsync(other, Title)).Editions, e => Assert.Equal(e.EditionId == EditionId, e.AlreadyAdded));
        Assert.Equal(EditionId, Assert.Single(await ShelfAsync(other)).EditionId);
    }

    [Fact]
    public async Task CatalogOutageDoesNotPreventReadingShelfOrRepeatingAnAdd()
    {
        await using var api = new ApiScenario(Title, Title);
        await SearchAsync(api.Client, Title);
        var added = await AddAsync(api.Client, EditionId);
        api.Catalog.FailSearches = true;
        using var unavailable = await api.Client.GetAsync(SearchPath(Title), Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
        var calls = api.Catalog.Requests.Count;
        var saved = Assert.Single(await ShelfAsync(api.Client));
        Assert.Equal(added.AddedAt, saved.AddedAt);
        using var repeated = await PostAsync(api.Client, EditionId);
        var duplicate = await ReadAsync<GalleryBook>(repeated, HttpStatusCode.OK);
        Assert.Equal(added.AddedAt, duplicate.AddedAt);
        Assert.Equal(calls, api.Catalog.Requests.Count);
    }

    [Fact]
    public async Task LegacyCoverRouteAndMultipartAddsAreNotSupported()
    {
        await using var api = new ApiScenario();
        using var form = new MultipartFormDataContent { { new StringContent(WorkId), "workId" }, { new StringContent(EditionId), "editionId" } };
        using var add = await api.Client.PostAsync(BooksPath, form, Ct);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, add.StatusCode);
        using var cover = await api.Client.GetAsync($"{BooksPath}/{EditionId}/cover", Ct);
        Assert.Equal(HttpStatusCode.NotFound, cover.StatusCode);
        Assert.Empty(api.Catalog.Requests);
        Assert.Equal(0, await DocumentCountAsync(api));
    }

    [Fact]
    public async Task AnonymousRequestsAreRejectedBeforeCatalogAccessOrBodyBindingButHealthIsPublic()
    {
        await using var api = new ApiScenario();
        using var anonymous = api.CreateClient(null);
        using var search = await anonymous.GetAsync(SearchPath(Title), Ct);
        using var editions = await anonymous.GetAsync(EditionsPath, Ct);
        using var gallery = await anonymous.GetAsync(BooksPath, Ct);
        using var add = await anonymous.PostAsync(BooksPath, null, Ct);
        using var health = await anonymous.GetAsync("/health", Ct);
        Assert.All(new[] { search, editions, gallery, add }, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Empty(api.Catalog.Requests);
        Assert.Equal(0, await DocumentCountAsync(api));
    }

    [Fact]
    public async Task LocalIdentityIsFixedAndDoesNotUseRequestHeaders()
    {
        await using var api = new ApiScenario(true);
        await AddAsync(api.Client, EditionId);
        using var spoofed = api.CreateClient("different-identity");
        spoofed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(EditionId, Assert.Single(await ShelfAsync(spoofed)).EditionId);
    }

    private static string SearchPath(string query) => $"/api/catalog/search?q={Uri.EscapeDataString(query)}";

    private static async Task<CatalogWork[]> SearchAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(SearchPath(query), Ct);
        return await ReadAsync<CatalogWork[]>(response, HttpStatusCode.OK);
    }

    private static async Task<GalleryBook[]> ShelfAsync(HttpClient client)
    {
        using var response = await client.GetAsync(BooksPath, Ct);
        return await ReadAsync<GalleryBook[]>(response, HttpStatusCode.OK);
    }

    private static async Task<GalleryBook> AddAsync(HttpClient client, string editionId)
    {
        using var response = await PostAsync(client, editionId);
        return await ReadAsync<GalleryBook>(response, HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, string editionId, string workId = WorkId) =>
        client.PostAsJsonAsync(BooksPath, new { workId, editionId }, Ct);

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        return Assert.IsType<T>(await response.Content.ReadFromJsonAsync<T>(Ct));
    }

    private static async Task<long> DocumentCountAsync(ApiScenario api) =>
        (await api.Store.Maintenance.SendAsync(new GetStatisticsOperation(), Ct)).CountOfDocuments;

    private static void AssertImportRequests(IEnumerable<Uri> requests, bool withGenres = false)
    {
        string[] expected = ["/works/OL10000W.json", "/search.json",
            .. withGenres ? new[] { "/tags/OL169T.json", "/tags/OL174T.json" } : [],
            "/works/OL10000W/editions.json?limit=100&offset=0",
            "/works/OL10000W/editions.json?limit=100&offset=2"];
        // The fixture verifies the exact search projection separately.
        Assert.Equal(expected, requests.Select(uri => uri.AbsolutePath == "/search.json" ? uri.AbsolutePath : uri.PathAndQuery));
    }
}
