using System.Text;
using Builder.Application.Abstractions;
using Builder.Domain.Connections;
using Builder.Domain.Pipelines;
using Builder.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Builds the authenticated <see cref="GitRemote"/> for a repository from its connection.</summary>
public sealed class GitRemotes(IAppDbContext db, ISecretProtector secrets)
{
    public async Task<GitRemote> ForRepositoryAsync(Repository repository, CancellationToken ct)
    {
        GitConnection? connection = repository.ConnectionId is { } id
            ? await db.Connections.AsNoTracking().IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id && c.OrgId == repository.OrgId, ct)
            : null;
        return new GitRemote(repository.Url, AuthorizationHeader(connection));
    }

    public async Task<(GitRemote Remote, Repository Repository)> ForPipelineAsync(Pipeline pipeline, CancellationToken ct)
    {
        var repository = await db.Repositories.AsNoTracking().IgnoreQueryFilters().FirstAsync(r => r.Id == pipeline.RepositoryId, ct);
        return (await ForRepositoryAsync(repository, ct), repository);
    }

    public string? AuthorizationHeader(GitConnection? connection)
    {
        if (connection?.TokenProtected is null) return null;
        var token = secrets.Unprotect(connection.TokenProtected);
        // Azure DevOps and GitHub both accept Basic auth with any user name and a PAT as the password.
        var user = connection.Username ?? "builder";
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
    }
}
