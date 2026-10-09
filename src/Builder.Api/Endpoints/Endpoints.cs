using System.Security.Claims;
using Builder.Api.Auth;
using Builder.Application.Dtos;
using Builder.Application.Services;
using Builder.Domain.Builds;

namespace Builder.Api.Endpoints;

public static class Endpoints
{
    private static string UserName(this ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    public static void MapBuilderApi(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(AuthSchemes.UserPolicy);

        api.MapGet("/dashboard", (DashboardService s, CancellationToken ct) => s.GetAsync(ct));

        // pipelines
        api.MapGet("/pipelines", (PipelineService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapGet("/pipelines/{id:guid}", (Guid id, PipelineService s, CancellationToken ct) => s.GetAsync(id, ct));
        api.MapPost("/pipelines", (PipelineInput input, PipelineService s, CancellationToken ct) => s.CreateAsync(input, ct));
        api.MapPut("/pipelines/{id:guid}", (Guid id, PipelineInput input, PipelineService s, CancellationToken ct) => s.UpdateAsync(id, input, ct));
        api.MapDelete("/pipelines/{id:guid}", async (Guid id, PipelineService s, BuildService b, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, b, ct);
            return Results.NoContent();
        });
        api.MapGet("/pipelines/{id:guid}/taskfile", (Guid id, string? branch, PipelineService s, CancellationToken ct) =>
            s.GetTaskfileAsync(id, branch, ct));
        api.MapPut("/pipelines/{id:guid}/taskfile", (Guid id, SaveTaskfileInput input, ClaimsPrincipal user, PipelineService s, CancellationToken ct) =>
            s.SaveTaskfileAsync(id, input, user.UserName(), ct));
        api.MapPost("/pipelines/plan", (PlanRequest request, PipelineService s) => s.Preview(request));
        api.MapPost("/pipelines/{id:guid}/builds", (Guid id, QueueBuildInput input, ClaimsPrincipal user, BuildService s, CancellationToken ct) =>
            s.QueueAsync(id, input, user.UserName(), ct));

        // builds
        api.MapGet("/builds", (Guid? pipelineId, BuildStatus? status, int? take, BuildService s, CancellationToken ct) =>
            s.ListAsync(pipelineId, status, take ?? 50, ct));
        api.MapGet("/builds/{id:guid}", (Guid id, BuildService s, CancellationToken ct) => s.GetAsync(id, ct));
        api.MapPost("/builds/{id:guid}/cancel", (Guid id, BuildService s, CancellationToken ct) => s.CancelAsync(id, ct));
        api.MapPost("/builds/{id:guid}/rerun", (Guid id, ClaimsPrincipal user, BuildService s, CancellationToken ct) =>
            s.RerunAsync(id, user.UserName(), ct));
        api.MapDelete("/builds/{id:guid}", async (Guid id, BuildService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapPost("/builds/{id:guid}/jobs/{jobId:guid}/approval",
            (Guid id, Guid jobId, ApprovalInput input, ClaimsPrincipal user, BuildService s, CancellationToken ct) =>
                s.DecideApprovalAsync(id, jobId, input, user.UserName(), ct));
        api.MapGet("/builds/{id:guid}/jobs/{jobId:guid}/logs", (Guid id, Guid jobId, long? after, BuildService s, CancellationToken ct) =>
            s.LogsAsync(id, jobId, after ?? 0, ct));
        api.MapGet("/artifacts/{id:guid}", async (Guid id, BuildService s, CancellationToken ct) =>
        {
            var (stream, name) = await s.OpenArtifactAsync(id, ct);
            return Results.File(stream, "application/gzip", name);
        });

        // agents
        api.MapGet("/agents", (AgentService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapPut("/agents/{id:guid}", (Guid id, AgentUpdateInput input, AgentService s, CancellationToken ct) => s.UpdateAsync(id, input, ct));
        api.MapDelete("/agents/{id:guid}", async (Guid id, AgentService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapGet("/agents/{id:guid}/metrics", (Guid id, AgentService s) => s.MetricsHistory(id));
        api.MapPost("/agents/{id:guid}/cleanup", async (Guid id, AgentCleanupInput input, AgentService s, CancellationToken ct) =>
            await s.RequestCleanupAsync(id, input, ct) ? Results.Accepted() : Results.Conflict(new { title = "The agent is offline." }));

        // environments & deployments
        api.MapGet("/environments", (EnvironmentService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapPost("/environments", (EnvironmentInput input, EnvironmentService s, CancellationToken ct) => s.CreateAsync(input, ct));
        api.MapPut("/environments/{id:guid}", (Guid id, EnvironmentInput input, EnvironmentService s, CancellationToken ct) => s.UpdateAsync(id, input, ct));
        api.MapDelete("/environments/{id:guid}", async (Guid id, EnvironmentService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapGet("/deployments", (Guid? environmentId, bool? active, DeploymentService s, CancellationToken ct) =>
            s.ListAsync(environmentId, active ?? false, ct));
        api.MapPost("/deployments/{id:guid}/destroy", (Guid id, DeploymentService s, CancellationToken ct) => s.DestroyAsync(id, ct));

        // connections
        api.MapGet("/connections", (ConnectionService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapPost("/connections", (ConnectionInput input, ConnectionService s, CancellationToken ct) => s.CreateAsync(input, ct));
        api.MapPut("/connections/{id:guid}", (Guid id, ConnectionInput input, ConnectionService s, CancellationToken ct) => s.UpdateAsync(id, input, ct));
        api.MapDelete("/connections/{id:guid}", async (Guid id, ConnectionService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapGet("/connections/{id:guid}/repositories", (Guid id, ConnectionService s, CancellationToken ct) => s.RepositoriesAsync(id, ct));

        api.MapPost("/cleanup", (CleanupInput input, CleanupService s, CancellationToken ct) => s.RunAsync(input, ct));

        // BFF-only: validates credentials and returns the user the BFF puts in its cookie.
        app.MapPost("/internal/login", async (LoginInput input, AuthService s, CancellationToken ct) =>
                await s.ValidateAsync(input, ct) is { } user ? Results.Ok(user) : Results.Unauthorized())
            .RequireAuthorization(AuthSchemes.ServicePolicy);
        app.MapPost("/internal/external-login", async (ExternalLoginInput input, AuthService s, CancellationToken ct) =>
                await s.ExternalLoginAsync(input, ct) is { } user ? Results.Ok(user) : Results.StatusCode(StatusCodes.Status403Forbidden))
            .RequireAuthorization(AuthSchemes.ServicePolicy);

        // agents: artifact upload/download
        var agent = app.MapGroup("/api/agent").RequireAuthorization(AuthSchemes.AgentPolicy);
        agent.MapPost("/jobs/{jobId:guid}/artifacts", async (Guid jobId, string name, HttpRequest request, AgentService s, CancellationToken ct) =>
        {
            await s.ArtifactUploadedAsync(jobId, name, request.Body, ct);
            return Results.NoContent();
        }).DisableAntiforgery();
        agent.MapGet("/artifacts/{id:guid}", async (Guid id, AgentService s, CancellationToken ct) =>
            Results.File(await s.OpenArtifactForAgentAsync(id, ct), "application/gzip"));

        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
    }
}
