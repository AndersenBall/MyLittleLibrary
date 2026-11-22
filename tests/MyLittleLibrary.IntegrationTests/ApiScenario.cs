using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

    public ApiScenario(params string[] expectedCatalogQueries)
    {
        Catalog = new OpenLibraryHandler(expectedCatalogQueries);
        Store = driver.CreateStore();
        factory = new ApiFactory(Store, Catalog);
    }

    public IDocumentStore Store { get; }
    public OpenLibraryHandler Catalog { get; }
    public HttpClient Client => client ??= factory.CreateClient(new() { AllowAutoRedirect = false });

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
            driver.Dispose();
        }
    }

    private sealed class ApiFactory(IDocumentStore store, OpenLibraryHandler catalog) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDocumentStore>();
                // The driver owns the store, not the host. Leave the initializer registered.
                services.AddSingleton(store);
                services.AddHttpClient(nameof(OpenLibraryClient))
                    .ConfigurePrimaryHttpMessageHandler(() => catalog);
            });
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
        Assert.Equal(expected, QueryHelpers.ParseQuery(uri.Query)["q"].ToString());

        if (FailSearches)
            throw new HttpRequestException("Simulated Open Library outage.");

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(SearchResponse, Encoding.UTF8, "application/json")
        });
    }
}
