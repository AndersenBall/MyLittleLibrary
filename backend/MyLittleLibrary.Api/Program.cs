using System.Net.Mail;
using Microsoft.AspNetCore.Mvc;

const string addLibraryBook = "addLibraryBook";

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SupportNonNullableReferenceTypes();
    options.NonNullableReferenceTypesAsRequired();
});
builder.Services.AddMemoryCache();
builder.AddRavenDb();
var contact = builder.Configuration["OpenLibrary:ContactEmail"]?.Trim();
if (!MailAddress.TryCreate(contact, out var address) || address.Address != contact ||
    contact!.Any(c => c is < '!' or > '~' or '(' or ')' or '\\')) contact = null;
builder.Services.AddSingleton(new CatalogRateLimit(contact is not null));
builder.Services.AddHttpClient<OpenLibraryClient>(http =>
{
    http.BaseAddress = new Uri("https://openlibrary.org/");
    http.DefaultRequestHeaders.UserAgent.ParseAdd(contact is null ? "MyLittleLibrary/1.0" : $"MyLittleLibrary/1.0 ({contact})");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting();
app.Use(async (context, next) =>
{
    // Run before the endpoint delegate reads and binds the multipart form.
    if (context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName != addLibraryBook)
    {
        await next(context);
        return;
    }

    var request = context.Request;
    if (request.Headers["Sec-Fetch-Site"] == "cross-site")
    {
        await Results.Problem("Add books from the library application.", statusCode: 403).ExecuteAsync(context);
        return;
    }
    if (!request.HasFormContentType || !request.ContentType!.StartsWith("multipart/form-data;", StringComparison.OrdinalIgnoreCase))
    {
        await Results.Problem("Multipart form data is required.", statusCode: 415).ExecuteAsync(context);
        return;
    }
    if (request.ContentLength > LibraryHandler.MaxAddRequestBytes)
    {
        await Results.Problem("Cover must be 5 MiB or smaller.", statusCode: 413).ExecuteAsync(context);
        return;
    }
    await next(context);
});

app.MapGet("/health", () => Results.Ok());
app.MapGet("/api/catalog/search", LibraryHandler.SearchCatalog)
    .WithName("searchCatalog").WithTags("Catalog").ProducesProblem(503);

var library = app.MapGroup("/api/library").WithTags("Library");
library.MapGet("/books", LibraryHandler.GetBooks).WithName("getLibraryBooks");
library.MapPost("/books", LibraryHandler.AddBook)
    .WithName(addLibraryBook)
    .Accepts<AddLibraryBookRequest>("multipart/form-data")
    .WithMetadata(new RequestSizeLimitAttribute(LibraryHandler.MaxAddRequestBytes))
    .DisableAntiforgery() // Anonymous, with cross-site browser requests rejected above.
    .Produces<GalleryBook>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status403Forbidden)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
    .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);
library.MapGet("/books/{editionId}/cover", LibraryHandler.GetCover)
    .WithName("getLibraryCover")
    .Produces<FileResult>(StatusCodes.Status200OK, "image/jpeg", "image/png", "image/webp");

app.Run();
