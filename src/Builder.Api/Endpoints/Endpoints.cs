using System.Security.Claims;
using Builder.Api.Auth;
using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Application.Services;
using Builder.Domain.Builds;

namespace Builder.Api.Endpoints;

public static class Endpoints
{
    public const string OrgHeader = "X-Org";

    private static string UserName(this ClaimsPrincipal user) => user.Identity?.Name ?? "unknown";

    public static void MapBuilderApi(this WebApplication app)
    {
        var user = app.MapGroup("/api").RequireAuthorization(AuthSchemes.UserPolicy);

        // ---- personal: who am I, my organizations, create one ----
        user.MapGet("/me", (ClaimsPrincipal u, OrganizationService s, CancellationToken ct) => s.MeAsync(u.UserName(), ct));
        user.MapPost("/orgs", (OrgInput input, ClaimsPrincipal u, OrganizationService s, CancellationToken ct) =>
            s.CreateAsync(u.UserName(), input, ct));
        user.MapPost("/me/password", async (ChangePasswordInput input, ClaimsPrincipal u, AuthService s, CancellationToken ct) =>
        {
            await s.ChangePasswordAsync(u.UserName(), input, ct);
            return Results.NoContent();
        });

        // ---- everything below acts in the organization named by the X-Org header ----
        var api = user.MapGroup("").AddEndpointFilter(RequireOrganization);

        api.MapPut("/org", (OrgInput input, OrganizationService s, CancellationToken ct) => s.RenameAsync(input, ct));
        api.MapPost("/org/agent-token", (OrganizationService s, CancellationToken ct) => s.RegenerateAgentTokenAsync(ct));
        api.MapGet("/org/members", (OrganizationService s, CancellationToken ct) => s.MembersAsync(ct));
        api.MapPost("/org/members", (AddMemberInput input, OrganizationService s, CancellationToken ct) => s.AddMemberAsync(input, ct));
        api.MapPut("/org/members/{userId:guid}", (Guid userId, ChangeRoleInput input, OrganizationService s, CancellationToken ct) =>
            s.ChangeRoleAsync(userId, input, ct));
        api.MapDelete("/org/members/{userId:guid}", async (Guid userId, OrganizationService s, CancellationToken ct) =>
        {
            await s.RemoveMemberAsync(userId, ct);
            return Results.NoContent();
        });

        api.MapGet("/dashboard", (DashboardService s, CancellationToken ct) => s.GetAsync(ct));

        // repositories and their runner files
        api.MapGet("/repositories", (RepositoryService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapGet("/repositories/{id:guid}", (Guid id, RepositoryService s, CancellationToken ct) => s.GetAsync(id, ct));
        api.MapPost("/repositories", (RepositoryInput input, RepositoryService s, CancellationToken ct) => s.CreateAsync(input, ct));
        api.MapPut("/repositories/{id:guid}", (Guid id, RepositoryInput input, RepositoryService s, CancellationToken ct) => s.UpdateAsync(id, input, ct));
        api.MapDelete("/repositories/{id:guid}", async (Guid id, RepositoryService s, BuildService b, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, b, ct);
            return Results.NoContent();
        });
        api.MapGet("/repositories/{id:guid}/branches", (Guid id, RepositoryService s, CancellationToken ct) => s.BranchesAsync(id, ct));
        api.MapGet("/repositories/{id:guid}/runner-files", (Guid id, string? branch, RepositoryService s, CancellationToken ct) =>
            s.RunnerFilesAsync(id, branch, ct));
        api.MapPost("/repositories/{id:guid}/runners", (Guid id, MapRunnersInput input, RepositoryService s, PipelineService p, CancellationToken ct) =>
            s.MapRunnersAsync(id, input, p, ct));

        // runners (mapped runner files)
        api.MapGet("/pipelines", (PipelineService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapGet("/pipelines/{id:guid}", (Guid id, PipelineService s, CancellationToken ct) => s.GetAsync(id, ct));
        api.MapPut("/pipelines/{id:guid}", (Guid id, PipelineInput input, PipelineService s, CancellationToken ct) => s.UpdateAsync(id, input, ct));
        api.MapDelete("/pipelines/{id:guid}", async (Guid id, PipelineService s, BuildService b, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, b, ct);
            return Results.NoContent();
        });
        api.MapGet("/pipelines/{id:guid}/taskfile", (Guid id, string? branch, PipelineService s, CancellationToken ct) =>
            s.GetTaskfileAsync(id, branch, ct));
        api.MapPut("/pipelines/{id:guid}/taskfile", (Guid id, SaveTaskfileInput input, ClaimsPrincipal u, PipelineService s, CancellationToken ct) =>
            s.SaveTaskfileAsync(id, input, u.UserName(), ct));
        api.MapPost("/pipelines/plan", (PlanRequest request, PipelineService s) => s.Preview(request));
        api.MapGet("/pipelines/{id:guid}/inputs", (Guid id, string? branch, string? entryTask, PipelineService s, CancellationToken ct) =>
            s.InputsAsync(id, branch, entryTask, ct));
        api.MapPost("/pipelines/{id:guid}/builds", (Guid id, QueueBuildInput input, ClaimsPrincipal u, BuildService s, CancellationToken ct) =>
            s.QueueAsync(id, input, u.UserName(), ct));

        // builds
        api.MapGet("/builds", (Guid? pipelineId, BuildStatus? status, int? take, BuildService s, CancellationToken ct) =>
            s.ListAsync(pipelineId, status, take ?? 50, ct));
        api.MapGet("/builds/{id:guid}", (Guid id, BuildService s, CancellationToken ct) => s.GetAsync(id, ct));
        api.MapPost("/builds/{id:guid}/cancel", (Guid id, BuildService s, CancellationToken ct) => s.CancelAsync(id, ct));
        api.MapPost("/builds/{id:guid}/rerun", (Guid id, ClaimsPrincipal u, BuildService s, CancellationToken ct) =>
            s.RerunAsync(id, u.UserName(), ct));
        api.MapDelete("/builds/{id:guid}", async (Guid id, BuildService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapPost("/builds/{id:guid}/jobs/{jobId:guid}/approval",
            (Guid id, Guid jobId, ApprovalInput input, ClaimsPrincipal u, BuildService s, CancellationToken ct) =>
                s.DecideApprovalAsync(id, jobId, input, u.UserName(), ct));
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

        // connections (Azure DevOps first: test, projects, repositories, branches)
        api.MapGet("/connections", (ConnectionService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapPost("/connections", (ConnectionInput input, ConnectionService s, CancellationToken ct) => s.CreateAsync(input, ct));
        api.MapPut("/connections/{id:guid}", (Guid id, ConnectionInput input, ConnectionService s, CancellationToken ct) => s.UpdateAsync(id, input, ct));
        api.MapDelete("/connections/{id:guid}", async (Guid id, ConnectionService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        api.MapPost("/connections/{id:guid}/test", (Guid id, ConnectionService s, CancellationToken ct) => s.TestAsync(id, ct));
        api.MapGet("/connections/{id:guid}/projects", (Guid id, ConnectionService s, CancellationToken ct) => s.ProjectsAsync(id, ct));
        api.MapGet("/connections/{id:guid}/repositories", (Guid id, string? project, ConnectionService s, CancellationToken ct) =>
            s.RepositoriesAsync(id, project, ct));
        api.MapGet("/connections/{id:guid}/branches", (Guid id, string url, ConnectionService s, CancellationToken ct) =>
            s.BranchesAsync(id, url, ct));

        // secrets (values are write-only)
        api.MapGet("/secrets", (SecretService s, CancellationToken ct) => s.ListAsync(ct));
        api.MapPost("/secrets", (SecretInput input, ClaimsPrincipal u, SecretService s, CancellationToken ct) => s.CreateAsync(input, u.UserName(), ct));
        api.MapPut("/secrets/{id:guid}", (Guid id, SecretInput input, ClaimsPrincipal u, SecretService s, CancellationToken ct) =>
            s.UpdateAsync(id, input, u.UserName(), ct));
        api.MapDelete("/secrets/{id:guid}", async (Guid id, SecretService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });

        api.MapPost("/cleanup", (CleanupInput input, CleanupService s, CancellationToken ct) => s.RunAsync(input, ct));

        // BFF-only: validates credentials and returns the user the BFF puts in its cookie.
        app.MapPost("/internal/login", async (LoginInput input, AuthService s, IConfiguration config, CancellationToken ct) =>
                !config.GetValue("Auth:PasswordSignIn", true) ? Results.StatusCode(StatusCodes.Status403Forbidden)
                : await s.ValidateAsync(input, ct) is { } u ? Results.Ok(u) : Results.Unauthorized())
            .RequireAuthorization(AuthSchemes.ServicePolicy);
        app.MapPost("/internal/external-login", async (ExternalLoginInput input, AuthService s, CancellationToken ct) =>
                await s.ExternalLoginAsync(input, ct) is { } u ? Results.Ok(u) : Results.StatusCode(StatusCodes.Status403Forbidden))
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

    /// <summary>
    /// Resolves the X-Org header to a membership of the signed-in user and sets the request's organization
    /// (which also scopes every EF query). Non-members get 404 so organization ids are not probeable.
    /// </summary>
    private static async ValueTask<object?> RequireOrganization(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!Guid.TryParse(http.Request.Headers[OrgHeader], out var orgId))
            return Results.Problem(title: "Select an organization (X-Org header).", statusCode: StatusCodes.Status400BadRequest,
                extensions: new Dictionary<string, object?> { ["code"] = "org_required" });
        var orgs = http.RequestServices.GetRequiredService<OrganizationService>();
        var (userId, role) = await orgs.MembershipAsync(http.User.UserName(), orgId, http.RequestAborted);
        if (role is null)
            return Results.Problem(title: "Organization not found.", statusCode: StatusCodes.Status404NotFound,
                extensions: new Dictionary<string, object?> { ["code"] = "org_not_found" });
        http.RequestServices.GetRequiredService<ICurrentOrg>().Set(orgId, role.Value, userId);
        return await next(context);
    }
}
