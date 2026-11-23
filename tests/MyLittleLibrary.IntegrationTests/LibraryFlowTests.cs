using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    private static readonly byte[] OtherPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQWfAfAAJ/AbTRw0zcAAAAAElFTkSuQmCC");
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task IsbnSearchReturnsBothEditionsAndMarksTheSavedEditionOnRepeatSearch()
    {
        await using var api = new ApiScenario(CatalogIsbnQuery, CatalogIsbnQuery);
        Assert.Empty(await ShelfAsync(api.Client));

        using var searchResponse = await api.Client.GetAsync(SearchPath("978-0-547-92822-7"), Ct);
        var (editions, searchJson) = await ReadJsonAsync<CatalogEdition[]>(searchResponse, HttpStatusCode.OK);
        AssertMetadataNotExposed(searchJson);
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
        var (added, addedJson) = await ReadJsonAsync<GalleryBook>(response, HttpStatusCode.Created);
        AssertMetadataNotExposed(addedJson);
        Assert.Single(api.Catalog.Requests);
        Assert.Equal(EditionId, added.EditionId);
        Assert.Equal(TitleQuery, added.Title);
        Assert.Equal(Author, Assert.Single(added.Authors));

        using var session = api.Store.OpenAsyncSession();
        var document = Assert.IsType<LibraryBook>(await session.LoadAsync<LibraryBook>("LibraryBooks/" + EditionId, Ct));
        Assert.Equal(EditionId, document.EditionId);
        Assert.Equal("OL10000W", document.WorkId);
        Assert.Equal(TitleQuery, document.Title);
        Assert.Equal(Isbn, Assert.Single(document.Isbn13));
        Assert.Equal(300, document.NumberOfPages);
        Assert.Equal(2012, document.PublicationYear);
        Assert.Equal(new[] { "The Hobbit editions", "Middle-earth" }, document.Series);
        Assert.Equal(new[] { "Children's books", "Fantasy", "Adventure" }, document.Tags);
        Assert.Equal(added.AddedAt, document.AddedAt);

        using var shelfResponse = await api.Client.GetAsync(BooksPath, Ct);
        var (shelf, shelfJson) = await ReadJsonAsync<GalleryBook[]>(shelfResponse, HttpStatusCode.OK);
        AssertMetadataNotExposed(shelfJson);
        var saved = Assert.Single(shelf);
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
    public async Task SparseRepeatedSearchRetainsCachedMetadataForAdd()
    {
        await using var api = new ApiScenario(TitleQuery, TitleQuery);
        api.Catalog.ReturnSparseAfterFirstSearch = true;
        await SearchAsync(api.Client, TitleQuery);
        await SearchAsync(api.Client, TitleQuery);

        using var form = BookForm(EditionId);
        using var accepted = await api.Client.PostAsync(BooksPath, form, Ct);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        using var session = api.Store.OpenAsyncSession();
        var book = Assert.IsType<LibraryBook>(await session.LoadAsync<LibraryBook>($"LibraryBooks/{EditionId}", Ct));

        Assert.Equal(300, book.NumberOfPages);
        Assert.Equal(2012, book.PublicationYear);
        Assert.Equal(new[] { "The Hobbit editions", "Middle-earth" }, book.Series);
        Assert.Equal(new[] { "Children's books", "Fantasy", "Adventure" }, book.Tags);
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
        Assert.Equal(2, await DocumentCountAsync(api));
        Assert.Single(api.Catalog.Requests);
    }

    [Fact]
    public async Task ConcurrentAddsByTheSameOwnerReturnOneEntry()
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
        Assert.Equal(2, await DocumentCountAsync(api));
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
        Assert.Equal(2, await DocumentCountAsync(api));
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

        using var repeat = BookForm(EditionId, OtherPng);
        using var response = await api.Client.PostAsync(BooksPath, repeat, Ct);
        var duplicate = await ReadAsync<GalleryBook>(response, HttpStatusCode.OK);
        Assert.Equal(EditionId, duplicate.EditionId);
        Assert.Equal(added.AddedAt, duplicate.AddedAt);
        Assert.Equal(added.CoverUrl, duplicate.CoverUrl);
        await AssertCoverAsync(api.Client, duplicate);
        Assert.Equal(callsAfterOutage, api.Catalog.Requests.Count);
    }

    [Fact]
    public async Task AnonymousRequestsAreRejectedBeforeFormBindingOrCatalogAccessButHealthIsPublic()
    {
        await using var api = new ApiScenario();
        using var anonymous = api.CreateClient(null);
        using var search = await anonymous.GetAsync(SearchPath(TitleQuery), Ct);
        using var gallery = await anonymous.GetAsync(BooksPath, Ct);
        using var add = await anonymous.PostAsync(BooksPath, null, Ct);
        using var cover = await anonymous.GetAsync($"{BooksPath}/{EditionId}/cover", Ct);
        using var health = await anonymous.GetAsync("/health", Ct);
        Assert.All(new[] { search, gallery, add, cover }, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Empty(api.Catalog.Requests);
        Assert.Equal(0, await DocumentCountAsync(api));
    }

    [Fact]
    public async Task TwoOwnersShareOneBookButNotEntriesAddedStatusOrUploads()
    {
        await using var api = new ApiScenario(TitleQuery, TitleQuery, TitleQuery);
        // These identities differ only by case; Raven document IDs themselves are case-insensitive.
        using var other = api.CreateClient("reader");
        await SearchAsync(api.Client, TitleQuery);
        using var firstForm = BookForm(EditionId, Png);
        using var secondForm = BookForm(EditionId, OtherPng);
        var responses = await Task.WhenAll(api.Client.PostAsync(BooksPath, firstForm, Ct), other.PostAsync(BooksPath, secondForm, Ct));
        using var first = responses[0];
        using var second = responses[1];
        var firstBook = await ReadAsync<GalleryBook>(first, HttpStatusCode.Created);
        var secondBook = await ReadAsync<GalleryBook>(second, HttpStatusCode.Created);
        Assert.Equal(firstBook.EditionId, secondBook.EditionId);
        Assert.Equal(3, await DocumentCountAsync(api));
        using (var session = api.Store.OpenAsyncSession())
        {
            var sharedBookId = $"LibraryBooks/{EditionId}";
            var shared = Assert.IsType<LibraryBook>(await session.LoadAsync<LibraryBook>(sharedBookId, Ct));
            Assert.Empty(session.Advanced.Attachments.GetNames(shared));
            var entries = (await session.Advanced.LoadStartingWithAsync<LibraryEntry>("LibraryEntries/", token: Ct)).ToArray();
            Assert.Equal(2, entries.Length);
            Assert.Equal(2, entries.Select(entry => entry.OwnerKey).Distinct().Count());
            Assert.All(entries, entry =>
            {
                Assert.Equal(sharedBookId, entry.BookId);
                Assert.Equal($"LibraryEntries/{entry.OwnerKey}/{EditionId}", session.Advanced.GetDocumentId(entry));
                Assert.Equal("cover", Assert.Single(session.Advanced.Attachments.GetNames(entry)).Name);
            });
        }

        using var firstOnlyForm = BookForm(IllustratedEditionId, Png);
        using var firstOnly = await api.Client.PostAsync(BooksPath, firstOnlyForm, Ct);
        Assert.Equal(HttpStatusCode.Created, firstOnly.StatusCode);
        Assert.Equal(new[] { EditionId, IllustratedEditionId }, (await ShelfAsync(api.Client)).Select(book => book.EditionId).Order());
        Assert.Equal(EditionId, Assert.Single(await ShelfAsync(other)).EditionId);
        Assert.All(await SearchAsync(api.Client, TitleQuery), book => Assert.True(book.AlreadyAdded));
        Assert.All(await SearchAsync(other, TitleQuery), book => Assert.Equal(book.EditionId == EditionId, book.AlreadyAdded));
        Assert.Equal(firstBook.CoverUrl, secondBook.CoverUrl);
        await AssertCoverAsync(api.Client, firstBook);
        await AssertCoverAsync(other, secondBook, OtherPng);
        using var hidden = await other.GetAsync($"{BooksPath}/{IllustratedEditionId}/cover", Ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task LegacyBookAndAttachmentArePreservedButNoOwnerIsInferred()
    {
        await using var api = new ApiScenario();
        var bookId = $"LibraryBooks/{EditionId}";
        string? etag;
        using (var seed = api.Store.OpenAsyncSession())
        using (var bytes = new MemoryStream(Png))
        {
            var legacyBook = new LibraryBook { EditionId = EditionId, Title = "Legacy metadata", CoverUrl = "https://covers.openlibrary.org/b/id/1234-M.jpg" };
            await seed.StoreAsync(legacyBook, bookId, Ct);
            seed.Advanced.Attachments.Store(bookId, "cover", bytes, "image/png");
            await seed.SaveChangesAsync(Ct);
            using var readBack = api.Store.OpenAsyncSession();
            etag = readBack.Advanced.GetChangeVectorFor(Assert.IsType<LibraryBook>(await readBack.LoadAsync<LibraryBook>(bookId, Ct)));
        }
        Assert.Empty(await ShelfAsync(api.Client));
        using var form = BookForm(EditionId);
        using var added = await api.Client.PostAsync(BooksPath, form, Ct);
        var entry = await ReadAsync<GalleryBook>(added, HttpStatusCode.Created);
        Assert.Equal("Legacy metadata", entry.Title);
        Assert.Equal("https://covers.openlibrary.org/b/id/1234-M.jpg", entry.CoverUrl);
        using var hidden = await api.Client.GetAsync($"{BooksPath}/{EditionId}/cover", Ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Empty(api.Catalog.Requests);
        Assert.Equal(2, await DocumentCountAsync(api));
        using var verify = api.Store.OpenAsyncSession();
        var shared = Assert.IsType<LibraryBook>(await verify.LoadAsync<LibraryBook>(bookId, Ct));
        Assert.Equal(etag, verify.Advanced.GetChangeVectorFor(shared));
        Assert.Equal("cover", Assert.Single(verify.Advanced.Attachments.GetNames(shared)).Name);
    }

    [Fact]
    public async Task LocalIdentityIsFixedAndDoesNotUseRequestHeaders()
    {
        await using var api = new ApiScenario(true, TitleQuery);
        await SearchAsync(api.Client, TitleQuery);
        using var form = BookForm(EditionId);
        using var added = await api.Client.PostAsync(BooksPath, form, Ct);
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        using var spoofed = api.CreateClient("different-identity");
        spoofed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-token");
        Assert.Equal(EditionId, Assert.Single(await ShelfAsync(spoofed)).EditionId);
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

    private static async Task<(T Value, string Json)> ReadJsonAsync<T>(HttpResponseMessage response, HttpStatusCode expected)
    {
        Assert.Equal(expected, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync(Ct);
        return (JsonSerializer.Deserialize<T>(json, WebJson)!, json);
    }

    private static void AssertMetadataNotExposed(string json)
    {
        foreach (var property in new[] { "numberOfPages", "publicationYear", "series", "tags" })
            Assert.DoesNotContain($"\"{property}\"", json, StringComparison.OrdinalIgnoreCase);
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

    private static async Task AssertCoverAsync(HttpClient client, GalleryBook book, byte[]? expectedBytes = null)
    {
        Assert.Equal($"{BooksPath}/{book.EditionId}/cover", book.CoverUrl);
        using var response = await client.GetAsync(book.CoverUrl, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(expectedBytes ?? Png, await response.Content.ReadAsByteArrayAsync(Ct));
    }
}
