using System.Collections.Concurrent;
using Builder.Api.Auth;
using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Application.Services;
using Builder.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Builder.Api.Hubs;

/// <summary>Live updates for the web UI (reached through the BFF).</summary>
[Authorize(Policy = AuthSchemes.UserPolicy)]
public sealed class UiHub : Hub
{
    public static string BuildGroup(Guid buildId) => $"build:{buildId}";

    public Task JoinBuild(Guid buildId) => Groups.AddToGroupAsync(Context.ConnectionId, BuildGroup(buildId));
    public Task LeaveBuild(Guid buildId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, BuildGroup(buildId));
}

public sealed class SignalRUiNotifier(IHubContext<UiHub> hub) : IUiNotifier
{
    public Task BuildUpdated(BuildSummaryDto build) => hub.Clients.All.SendAsync("BuildUpdated", build);
    public Task JobUpdated(JobDto job) => hub.Clients.Group(UiHub.BuildGroup(job.BuildId)).SendAsync("JobUpdated", job);
    public Task Log(Guid buildId, IReadOnlyList<LogLineDto> lines) => hub.Clients.Group(UiHub.BuildGroup(buildId)).SendAsync("Log", buildId, lines);
    public Task AgentsUpdated(IReadOnlyList<AgentDto> agents) => hub.Clients.All.SendAsync("AgentsUpdated", agents);
    public Task DeploymentUpdated(DeploymentDto deployment) => hub.Clients.All.SendAsync("DeploymentUpdated", deployment);
}

/// <summary>Which SignalR connection belongs to which agent.</summary>
public sealed class AgentConnections
{
    private readonly ConcurrentDictionary<Guid, string> _byAgent = new();

    public void Set(Guid agentId, string connectionId) => _byAgent[agentId] = connectionId;
    public void Remove(Guid agentId, string connectionId) => _byAgent.TryRemove(new(agentId, connectionId));
    public string? Get(Guid agentId) => _byAgent.TryGetValue(agentId, out var c) ? c : null;
}

[Authorize(Policy = AuthSchemes.AgentPolicy)]
public sealed class AgentHub(AgentService agents, AgentConnections connections) : Hub
{
    private Guid AgentId => Context.Items.TryGetValue("agentId", out var id) && id is Guid g
        ? g : throw new HubException("Register first.");

    public async Task<AgentWelcome> Register(AgentHello hello)
    {
        var welcome = await agents.RegisterAsync(Context.ConnectionId, hello, Context.ConnectionAborted);
        Context.Items["agentId"] = welcome.AgentId;
        connections.Set(welcome.AgentId, Context.ConnectionId);
        return welcome;
    }

    public Task Heartbeat(AgentMetrics metrics) => agents.HeartbeatAsync(AgentId, metrics, Context.ConnectionAborted);
    public Task JobStarted(Guid jobId) => agents.JobStartedAsync(jobId, Context.ConnectionAborted);
    public Task JobLog(Guid jobId, List<LogChunk> lines) => agents.JobLogAsync(jobId, lines, Context.ConnectionAborted);
    public Task JobCompleted(JobResult result) => agents.JobCompletedAsync(result, CancellationToken.None);
    public Task TeardownCompleted(TeardownResult result) => agents.TeardownCompletedAsync(result, CancellationToken.None);
    public Task CleanupCompleted(CleanupResult result) => Task.CompletedTask;

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (Context.Items.TryGetValue("agentId", out var id) && id is Guid agentId)
        {
            connections.Remove(agentId, Context.ConnectionId);
            await agents.DisconnectedAsync(agentId, Context.ConnectionId, CancellationToken.None);
        }
        await base.OnDisconnectedAsync(exception);
    }
}

public sealed class SignalRAgentGateway(IHubContext<AgentHub> hub, AgentConnections connections, ILogger<SignalRAgentGateway> log)
    : IAgentGateway
{
    public bool IsConnected(Guid agentId) => connections.Get(agentId) is not null;

    public async Task<bool> AssignJobAsync(Guid agentId, JobAssignment assignment, CancellationToken ct)
    {
        if (connections.Get(agentId) is not { } connectionId) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await hub.Clients.Client(connectionId).InvokeAsync<bool>(AgentHubNames.AssignJob, assignment, timeout.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogWarning(ex, "Agent {Agent} did not accept job {Job}", agentId, assignment.JobId);
            return false;
        }
    }

    public Task CancelJobAsync(Guid agentId, Guid jobId, CancellationToken ct) =>
        connections.Get(agentId) is { } c ? hub.Clients.Client(c).SendAsync(AgentHubNames.CancelJob, jobId, ct) : Task.CompletedTask;

    public async Task<bool> CleanupAsync(Guid agentId, CleanupRequest request, CancellationToken ct)
    {
        if (connections.Get(agentId) is not { } c) return false;
        await hub.Clients.Client(c).SendAsync(AgentHubNames.Cleanup, request, ct);
        return true;
    }

    public async Task<bool> TeardownAsync(Guid agentId, TeardownRequest request, CancellationToken ct)
    {
        if (connections.Get(agentId) is not { } c) return false;
        await hub.Clients.Client(c).SendAsync(AgentHubNames.Teardown, request, ct);
        return true;
    }
}
