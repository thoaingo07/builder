using Builder.Application.Abstractions;
using Builder.Infrastructure.Git;
using Builder.Infrastructure.Persistence;
using Builder.Infrastructure.Services;
using Builder.Infrastructure.Taskfiles;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Builder.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Builder")
            ?? throw new InvalidOperationException("ConnectionStrings:Builder is not configured.");
        services.AddDbContext<AppDbContext>(o => o.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

        services.Configure<BuilderStorageOptions>(config.GetSection("Storage"));
        var dataDir = Path.GetFullPath(config["Storage:DataDirectory"] ?? "data");
        services.AddDataProtection()
            .SetApplicationName("Builder")
            .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();
        services.AddSingleton<GitCli>();
        services.AddHttpClient<AzureDevOpsGit>(c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false }); // sign-in redirect = bad PAT
        services.AddTransient<IGitService, GitRouter>();
        services.AddTransient<IGitHostStatus>(sp => sp.GetRequiredService<AzureDevOpsGit>());
        services.AddSingleton<ITaskfilePlanner, TaskfilePlanner>();
        services.AddSingleton<IArtifactStore, FileArtifactStore>();
        services.AddSingleton<IAgentMetricsStore, InMemoryAgentMetricsStore>();
        services.AddSingleton<IBuildLock, BuildLock>();
        services.AddSingleton<ISchedulerSignal, SchedulerSignal>();
        services.AddHttpClient<IAzureDevOpsClient, AzureDevOpsClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.Configure<AzureOptions>(config.GetSection("Azure"));
        services.AddHttpClient<IEntraTokens, EntraTokens>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IAcrTokens, AcrTokens>(c => c.Timeout = TimeSpan.FromSeconds(30));
        services.AddHttpClient<IRegistryLogins, RegistryLogins>(c => c.Timeout = TimeSpan.FromSeconds(20));
        return services;
    }
}
