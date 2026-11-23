using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Raven.Client.Documents;
using Raven.Embedded;
using Raven.TestDriver;
using Xunit;

namespace MyLittleLibrary.IntegrationTests;

internal sealed class ApiScenario : IAsyncDisposable
{
    private readonly TestRavenDriver driver = new();
    private readonly ApiFactory factory;
    private HttpClient? client;
    private readonly bool useLocalIdentity;

    public ApiScenario(params string[] expectedCatalogQueries) : this(false, expectedCatalogQueries) { }

    public ApiScenario(bool useLocalIdentity, params string[] expectedCatalogQueries)
    {
        Catalog = new OpenLibraryHandler(expectedCatalogQueries);
        Store = driver.CreateStore();
        factory = new ApiFactory(Store, Catalog, Clock, useLocalIdentity);
        this.useLocalIdentity = useLocalIdentity;
    }

    public IDocumentStore Store { get; }
    public OpenLibraryHandler Catalog { get; }
    public TestClock Clock { get; } = new();
    public HttpClient Client => client ??= CreateClient(useLocalIdentity ? null : "Reader");

    public HttpClient CreateClient(string? subject)
    {
        var result = factory.CreateClient(new() { AllowAutoRedirect = false });
        if (subject is not null) result.DefaultRequestHeaders.Add(TestIdentityHandler.SubjectHeader, subject);
        return result;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            // The factory disposes its clients before stopping the API host.
            await factory.DisposeAsync();
        }
        finally
        {
            Catalog.Dispose();
            // TestDriver deletes this scenario's database; its embedded server is shared by the test process.
            driver.Dispose();
        }
    }

    internal sealed class TestClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public TimeSpan Elapsed { get; set; }
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + Elapsed;
    }

    private sealed class ApiFactory(IDocumentStore store, OpenLibraryHandler catalog, TestClock clock, bool useLocalIdentity) : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                [HostDefaults.EnvironmentKey] = "Local",
                ["Auth:EnableLocalDevIdentity"] = "true"
            }));
            return base.CreateHost(builder);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureTestServices(services =>
            {
                if (!useLocalIdentity)
                    services.AddAuthentication(TestIdentityHandler.SchemeName)
                        .AddScheme<AuthenticationSchemeOptions, TestIdentityHandler>(TestIdentityHandler.SchemeName, _ => { });
                services.RemoveAll<IDocumentStore>();
                // Register the externally owned instance without transferring disposal to the host.
                // Leave the initializer registered so it checks the test database on startup.
                services.AddSingleton(store);
                services.Configure<MemoryCacheOptions>(options => options.Clock = clock);
                services.AddHttpClient(nameof(OpenLibraryClient))
                    .ConfigurePrimaryHttpMessageHandler(() => catalog);
            });
        }
    }

    // TestServer-only authentication. Does not register or exercise JWT bearer or Auth0.
    private sealed class TestIdentityHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "FixtureIdentity";
        public const string SubjectHeader = "X-Test-Subject";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(SubjectHeader, out var subject)) return Task.FromResult(AuthenticateResult.NoResult());
            var user = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("iss", "urn:mylittlelibrary:tests"), new Claim("sub", subject.ToString())], SchemeName));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, SchemeName)));
        }
    }

    private sealed class TestRavenDriver : RavenTestDriver
    {
        static TestRavenDriver() => ConfigureServer(new Raven.TestDriver.TestServerOptions
        {
            Licensing = new ServerOptions.LicensingOptions
            {
                ThrowOnInvalidOrMissingLicense = false,
                DisableAutoUpdate = true,
                DisableAutoUpdateFromApi = true
            }
        });

        public IDocumentStore CreateStore() => GetDocumentStore(database: $"MyLittleLibraryTests-{Guid.NewGuid():N}");
    }
}

internal sealed class OpenLibraryHandler(string[] expectedQueries) : HttpMessageHandler
{
    private const string SearchFields = "key,title,author_name,subject,series_name,first_publish_year,editions,editions.key,editions.title,editions.isbn,editions.publisher,editions.publish_date,editions.cover_i";
    private readonly ConcurrentQueue<string> pendingQueries = new(expectedQueries);
    public ConcurrentQueue<Uri> Requests { get; } = new();
    public string[][] EditionPages { get; set; } = [["OL100001M", "OL100002M"]];
    public int? FailEditionOffset { get; set; }
    public bool FailSearches { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = Assert.IsType<Uri>(request.RequestUri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://openlibrary.org", uri.GetLeftPart(UriPartial.Authority));
        Requests.Enqueue(uri);
        return Task.FromResult(uri.AbsolutePath switch
        {
            "/search.json" => Search(uri),
            "/works/OL10000W.json" when uri.Query.Length == 0 => Work(),
            "/works/OL10000W/editions.json" => Editions(uri),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected Open Library request: {uri}")
        });
    }

    private HttpResponseMessage Search(Uri uri)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(3, query.Count);
        Assert.Equal("12", Assert.Single(query["limit"]));
        var fields = Assert.IsType<string>(Assert.Single(query["fields"]));
        Assert.Equal(SearchFields.Split(',').Order(StringComparer.Ordinal), fields.Split(',').Order(StringComparer.Ordinal));
        Assert.True(pendingQueries.TryDequeue(out var expected), $"Unexpected catalog search: {uri}");
        Assert.Equal(expected, Assert.Single(query["q"]));
        if (FailSearches) throw new HttpRequestException("Simulated Open Library outage.");
        return Json("""
        { "docs": [{
          "key": "/works/OL10000W", "title": "The Hobbit", "author_name": ["J. R. R. Tolkien"],
          "subject": ["Fantasy", "Adventure"], "series_name": ["Middle-earth"], "first_publish_year": 1937,
          "editions": { "docs": [
            { "key": "/books/OL100001M", "title": "The Hobbit", "isbn": ["054792822X", "9780547928227"],
              "publisher": ["Mariner Books"], "publish_date": ["2012"], "cover_i": 1234 },
            { "key": "/books/OL100002M", "title": "The Hobbit (illustrated)" }
          ] }
        }] }
        """);
    }

    private static HttpResponseMessage Work() => Json("""
        { "key": "/works/OL10000W", "title": "The Hobbit", "first_publish_date": "January 1938",
          "authors": [{ "author": { "key": "/authors/OL26320A" } }], "subjects": ["Fantasy", "Dragons"] }
        """);

    private HttpResponseMessage Editions(Uri uri)
    {
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(2, query.Count);
        Assert.Contains(Assert.Single(query["limit"]), new[] { "25", "100" });
        var offset = int.Parse(Assert.Single(query["offset"])!);
        if (FailEditionOffset == offset) throw new HttpRequestException("Simulated Open Library outage.");
        var pageOffset = 0;
        foreach (var page in EditionPages)
        {
            if (offset == pageOffset)
            {
                var docs = string.Join(',', page.Select(id => id switch
                {
                    "OL100001M" => """{ "key": "/books/OL100001M", "title": "The Hobbit", "publish_date": "2012", "isbn_13": ["9780547928227"], "publishers": ["Mariner Books"], "covers": [1234] }""",
                    "OL100002M" => """{ "key": "/books/OL100002M", "title": "The Hobbit (illustrated)", "publish_date": "May 2015" }""",
                    _ => $$"""{ "key": "/books/{{id}}", "title": "The Hobbit (paperback)" }"""
                }));
                return Json($"{{\"size\":{EditionPages.Sum(x => x.Length)},\"entries\":[{docs}]}}");
            }
            pageOffset += page.Length;
        }
        throw new Xunit.Sdk.XunitException($"Unexpected editions offset: {offset}");
    }

    private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(content, Encoding.UTF8, "application/json")
    };
}
