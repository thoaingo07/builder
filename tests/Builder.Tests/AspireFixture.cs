using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Builder.Tests;

/// <summary>
/// Starts the Builder AppHost once for the test run (profile "backend": PostgreSQL, migrator, API, BFF,
/// two agents; ephemeral data, random ports). Needs Docker or Podman for the PostgreSQL container.
/// </summary>
public sealed class AspireFixture : IAsyncLifetime
{
    public DistributedApplication App { get; private set; } = null!;
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Builder_AppHost>(
            ["--Builder:Profile=backend", "--Builder:Ephemeral=true"]);
        builder.Services.AddLogging(l => l.SetMinimumLevel(LogLevel.Warning));
        builder.Services.ConfigureHttpClientDefaults(c => c.AddStandardResilienceHandler());

        App = await builder.BuildAsync();
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        await App.StartAsync(startup.Token);
        await App.ResourceNotifications.WaitForResourceHealthyAsync("builder", startup.Token);
        await App.ResourceNotifications.WaitForResourceAsync("bff", KnownResourceStates.Running, startup.Token);
        ConnectionString = await App.GetConnectionStringAsync("builder", startup.Token)
            ?? throw new InvalidOperationException("No connection string for 'builder'.");
    }

    public async Task DisposeAsync()
    {
        if (App is not null) await App.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class AspireCollection : ICollectionFixture<AspireFixture>
{
    public const string Name = "aspire";
}
