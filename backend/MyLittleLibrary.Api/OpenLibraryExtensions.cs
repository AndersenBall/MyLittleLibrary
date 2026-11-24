internal static class OpenLibraryExtensions
{
    public static IServiceCollection AddOpenLibrary(this IServiceCollection services, string? contact)
    {
        services.AddSingleton(new CatalogRateLimit(contact is not null));
        services.AddHttpClient<OpenLibraryClient>(http =>
        {
            http.BaseAddress = new Uri("https://openlibrary.org/");
            http.DefaultRequestHeaders.UserAgent.ParseAdd(contact is null ? "MyLittleLibrary/1.0" : $"MyLittleLibrary/1.0 ({contact})");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        return services;
    }
}