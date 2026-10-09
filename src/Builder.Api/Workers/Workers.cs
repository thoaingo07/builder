using Builder.Application.Abstractions;
using Builder.Application.Services;

namespace Builder.Api.Workers;

/// <summary>Plans queued builds (git fetch + Taskfile parse), one at a time.</summary>
public sealed class PlannerWorker(IServiceScopeFactory scopes, ISchedulerSignal signal, ILogger<PlannerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool planned;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    planned = await scope.ServiceProvider.GetRequiredService<BuildPlanningService>().PlanNextAsync(ct);
                    if (planned) signal.Wake(); // new jobs may be queued
                } while (planned && !ct.IsCancellationRequested);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Planner loop failed");
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
    }
}

/// <summary>Assigns queued jobs to agents whenever something changes (and every 2 s as a fallback).</summary>
public sealed class SchedulerWorker(IServiceScopeFactory scopes, ISchedulerSignal signal, ILogger<SchedulerWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SchedulerService>().AssignQueuedJobsAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Scheduler loop failed");
            }
            await signal.WaitAsync(TimeSpan.FromSeconds(2), ct);
        }
    }
}

/// <summary>Broadcasts agent metrics to the UI and fails jobs of agents that vanished.</summary>
public sealed class AgentMonitorWorker(IServiceScopeFactory scopes, ILogger<AgentMonitorWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        var tick = 0;
        while (await timer.WaitForNextTickAsync(ct))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var agents = scope.ServiceProvider.GetRequiredService<AgentService>();
                await agents.PublishAgentsAsync(ct);
                if (++tick % 5 == 0) await agents.FailJobsOfLostAgentsAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "Agent monitor failed");
            }
        }
    }
}
