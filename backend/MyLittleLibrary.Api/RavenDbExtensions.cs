using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Exceptions;
using Raven.Client.Exceptions.Database;
using Raven.Client.ServerWide;
using Raven.Client.ServerWide.Operations;

internal static class RavenDbExtensions
{
    public static WebApplicationBuilder AddRavenDb(this WebApplicationBuilder builder)
    {
        var url = builder.Configuration["RavenDb:Url"] ?? "http://ravendb:8080";
        var database = builder.Configuration["RavenDb:Database"] ?? "MyLittleLibrary";

        builder.Services.AddSingleton<IDocumentStore>(_ =>
            new DocumentStore { Urls = [url], Database = database }.Initialize());
        builder.Services.AddHostedService<RavenDatabaseInitializer>();

        return builder;
    }
}

internal sealed class RavenDatabaseInitializer(IDocumentStore store) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await store.Maintenance.SendAsync(new GetStatisticsOperation(), cancellationToken);
                return;
            }
            catch (DatabaseDoesNotExistException)
            {
                try { await store.Maintenance.Server.SendAsync(new CreateDatabaseOperation(new DatabaseRecord(store.Database)), cancellationToken); }
                catch (ConcurrencyException) { /* Another instance is creating this database. */ }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt < 14 &&
                ex is HttpRequestException or TimeoutException or AllTopologyNodesDownException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
