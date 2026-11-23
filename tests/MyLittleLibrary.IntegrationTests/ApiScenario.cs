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
        factory = new ApiFactory(Store, Catalog, useLocalIdentity);
        this.useLocalIdentity = useLocalIdentity;
    }

    public IDocumentStore Store { get; }
    public OpenLibraryHandler Catalog { get; }
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

    private sealed class ApiFactory(IDocumentStore store, OpenLibraryHandler catalog, bool useLocalIdentity) : WebApplicationFactory<Program>
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
    private const string SearchFields = "key,title,author_name,editions,editions.key,editions.title,editions.author_name,editions.isbn,editions.publisher,editions.publish_date,editions.cover_i";

    private const string SearchResponse = """
        {
          "docs": [{
            "key": "/works/OL10000W",
            "title": "The Hobbit",
            "author_name": ["J. R. R. Tolkien"],
            "editions": { "docs": [
              {
                "key": "/books/OL100001M",
                "title": "The Hobbit",
                "isbn": ["054792822X", "9780547928227"],
                "publisher": ["Mariner Books"],
                "publish_date": ["2012"],
                "cover_i": 1234
              },
              {
                "key": "/books/OL100002M",
                "title": "The Hobbit (illustrated)"
              }
            ] }
          }]
        }
        """;

    private readonly ConcurrentQueue<string> pendingQueries = new(expectedQueries);
    public ConcurrentQueue<Uri> Requests { get; } = new();
    public bool FailSearches { get; set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = Assert.IsType<Uri>(request.RequestUri);
        Requests.Enqueue(uri);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://openlibrary.org/search.json", uri.GetLeftPart(UriPartial.Path));
        Assert.True(pendingQueries.TryDequeue(out var expected), $"Unexpected catalog request: {uri}");
        var query = QueryHelpers.ParseQuery(uri.Query);
        Assert.Equal(3, query.Count);
        Assert.Equal(expected, Assert.Single(query["q"]));
        Assert.Equal("12", Assert.Single(query["limit"]));
        var fields = Assert.IsType<string>(Assert.Single(query["fields"]));
        Assert.Equal(SearchFields.Split(',').Order(StringComparer.Ordinal), fields.Split(',').Order(StringComparer.Ordinal));

        if (FailSearches)
            throw new HttpRequestException("Simulated Open Library outage.");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SearchResponse, Encoding.UTF8, "application/json")
        });
    }
}
