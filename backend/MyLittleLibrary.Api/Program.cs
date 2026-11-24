using System.Net.Mail;
using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SupportNonNullableReferenceTypes();
    options.NonNullableReferenceTypesAsRequired();
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
});
builder.AddLibraryAuthentication();
builder.AddRavenDb();
var contact = builder.Configuration["OpenLibrary:ContactEmail"]?.Trim();
if (!MailAddress.TryCreate(contact, out var address) || address.Address != contact ||
    contact!.Any(c => c is < '!' or > '~' or '(' or ')' or '\\')) contact = null;
builder.Services.AddOpenLibrary(contact);

var app = builder.Build();

app.UseRouting();
app.Use(async (context, next) =>
{
    if (context.GetEndpoint() is { } endpoint && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
        context.Response.Headers.CacheControl = "private, no-store";
    await next(context);
});
app.UseAuthentication();
app.UseAuthorization();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/health", () => Results.Ok()).AllowAnonymous();

var api = app.MapGroup("/api");

var catalog = api.MapGroup("/catalog").WithTags("Catalog");
catalog.MapGet("/search", CatalogHandler.SearchCatalog)
    .WithName("searchCatalog")
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
catalog.MapGet("/works/{workId}/editions", CatalogHandler.GetCatalogEditions)
    .WithName("getCatalogEditions")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

var library = api.MapGroup("/library").WithTags("Library");
library.MapGet("/books", LibraryHandler.GetBooks)
    .WithName("getLibraryBooks");
library.MapPost("/books", LibraryHandler.AddBook)
    .WithName("addLibraryBook")
    .Produces<GalleryBook>(StatusCodes.Status201Created)
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .Produces(StatusCodes.Status415UnsupportedMediaType)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

var readingHistory = api.MapGroup("/reading-history").WithTags("Reading History");
readingHistory.MapPost("/books", ReadingHistoryHandler.MarkAsRead)
    .WithName("markBookAsRead")
    .ProducesProblem(StatusCodes.Status400BadRequest)
    .ProducesProblem(StatusCodes.Status404NotFound)
    .ProducesProblem(StatusCodes.Status409Conflict)
    .Produces(StatusCodes.Status415UnsupportedMediaType)
    .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

app.Run();

public partial class Program { }
