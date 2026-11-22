using System.Net.Mail;
using Microsoft.AspNetCore.Http.HttpResults;
using Raven.Client.Documents;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SupportNonNullableReferenceTypes();
    options.NonNullableReferenceTypesAsRequired();
});
builder.Services.AddMemoryCache();
builder.Services.AddLibraryStorage(builder.Configuration);
var contact = builder.Configuration["OpenLibrary:ContactEmail"]?.Trim();
if (!MailAddress.TryCreate(contact, out var address) || address.Address != contact ||
    contact!.Any(c => c is < '!' or > '~' or '(' or ')' or '\\')) contact = null;
builder.Services.AddSingleton(new CatalogRateLimit(contact is not null));
builder.Services.AddHttpClient<CatalogClient>(http =>
{
    http.BaseAddress = new Uri("https://openlibrary.org/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd(contact is null ? "MyLittleLibrary/1.0" : $"MyLittleLibrary/1.0 ({contact})");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok());
app.MapGet("/api/catalog/search", SearchCatalog).WithName("searchCatalog").WithTags("Catalog").ProducesProblem(503);
app.MapLibraryEndpoints();

app.Run();

static async Task<Results<Ok<CatalogEdition[]>, ValidationProblem, ProblemHttpResult>> SearchCatalog(
    string? q, CatalogClient catalog, IDocumentStore store, CancellationToken ct)
{
    var query = q?.Trim();
    if (string.IsNullOrEmpty(query) || query.Length > 200)
        return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["q"] = ["Enter a title, author, or ISBN (up to 200 characters)."] });

    CatalogEdition[] editions;
    try { editions = await catalog.SearchAsync(query, ct); }
    catch (CatalogUnavailableException ex)
    {
        return TypedResults.Problem(title: ex.Message, statusCode: 503);
    }

    if (editions.Length == 0) return TypedResults.Ok(editions);
    using var session = store.OpenAsyncSession();
    var saved = await session.LoadAsync<LibraryBook>(editions.Select(e => LibraryBook.DocumentId(e.EditionId)), ct);
    return TypedResults.Ok(editions.Select(e => e with { AlreadyAdded = saved[LibraryBook.DocumentId(e.EditionId)] is not null }).ToArray());
}
