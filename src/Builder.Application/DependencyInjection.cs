using Builder.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Builder.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services) => services
        .AddScoped<GitRemotes>()
        .AddScoped<BuildEvents>()
        .AddScoped<BuildService>()
        .AddScoped<BuildPlanningService>()
        .AddScoped<SchedulerService>()
        .AddScoped<AgentService>()
        .AddScoped<PipelineService>()
        .AddScoped<EnvironmentService>()
        .AddScoped<DeploymentService>()
        .AddScoped<ConnectionService>()
        .AddScoped<CleanupService>()
        .AddScoped<DashboardService>()
        .AddScoped<AuthService>();
}
