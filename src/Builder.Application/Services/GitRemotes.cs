using System.Text;
using Builder.Application.Abstractions;
using Builder.Domain.Connections;
using Builder.Domain.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Builds the authenticated <see cref="GitRemote"/> for a pipeline from its connection.</summary>
public sealed class GitRemotes(IAppDbContext db, ISecretProtector secrets)
{
    public async Task<GitRemote> ForPipelineAsync(Pipeline pipeline, CancellationToken ct)
    {
        GitConnection? connection = pipeline.ConnectionId is { } id
            ? await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct)
            : null;
        return new GitRemote(pipeline.RepositoryUrl, AuthorizationHeader(connection));
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
