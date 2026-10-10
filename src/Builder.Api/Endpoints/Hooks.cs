using System.Text;
using System.Text.Json.Nodes;
using Builder.Application.Services;

namespace Builder.Api.Endpoints;

/// <summary>
/// Azure DevOps service hooks (git.push, git.pullrequest.created/updated). Anonymous, but every call must carry the
/// repository's webhook secret in X-Builder-Hook (or as the Basic-auth password).
/// </summary>
public static class Hooks
{
    public static void MapHooks(this WebApplication app)
    {
        app.MapPost("/hooks/azure-devops/{repositoryId:guid}", async (Guid repositoryId, HttpRequest request, TriggerService triggers,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var repo = await triggers.VerifyHookAsync(repositoryId, Secret(request), ct);
            if (repo is null) return Results.Unauthorized();

            var payload = await JsonNode.ParseAsync(request.Body, cancellationToken: ct);
            var eventType = payload?["eventType"]?.GetValue<string>();
            var resource = payload?["resource"];
            var started = new List<Guid>();
            switch (eventType)
            {
                case "git.push":
                    foreach (var update in resource?["refUpdates"]?.AsArray() ?? [])
                    {
                        var name = update?["name"]?.GetValue<string>() ?? "";
                        var after = update?["newObjectId"]?.GetValue<string>() ?? "";
                        if (!name.StartsWith("refs/heads/") || after.Trim('0').Length == 0) continue; // tags, deleted branches
                        started.AddRange(await triggers.OnPushAsync(repo, name["refs/heads/".Length..],
                            update?["oldObjectId"]?.GetValue<string>() ?? "", after,
                            resource?["pushedBy"]?["displayName"]?.GetValue<string>() ?? "someone", ct));
                    }
                    break;
                case "git.pullrequest.created" or "git.pullrequest.updated":
                    if (resource?["status"]?.GetValue<string>() is not ("active" or null)) break; // completed / abandoned
                    var source = resource?["lastMergeSourceCommit"]?["commitId"]?.GetValue<string>();
                    if (source is null) break;
                    started.AddRange(await triggers.OnPullRequestAsync(repo,
                        resource!["pullRequestId"]!.GetValue<int>(),
                        Branch(resource["sourceRefName"]), Branch(resource["targetRefName"]),
                        source, resource["lastMergeCommit"]?["commitId"]?.GetValue<string>(),
                        resource["createdBy"]?["displayName"]?.GetValue<string>() ?? "someone", ct));
                    break;
                default:
                    logs.CreateLogger("Builder.Hooks").LogInformation("Ignoring Azure DevOps event {Event}", eventType);
                    break;
            }
            return Results.Accepted(value: new { builds = started });
        }).AllowAnonymous();
    }

    private static string Branch(JsonNode? refName) => (refName?.GetValue<string>() ?? "").Replace("refs/heads/", "");

    private static string? Secret(HttpRequest request)
    {
        if (request.Headers[TriggerService.HookHeader].ToString() is { Length: > 0 } header) return header;
        var auth = request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth[6..]));
            return decoded[(decoded.IndexOf(':') + 1)..];
        }
        catch (FormatException) { return null; }
    }
}
