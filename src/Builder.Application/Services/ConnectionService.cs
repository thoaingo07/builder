using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Connections;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

public sealed class ConnectionService(IAppDbContext db, IClock clock, ISecretProtector secrets, IAzureDevOpsClient azureDevOps)
{
    public async Task<List<ConnectionDto>> ListAsync(CancellationToken ct) =>
        (await db.Connections.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)).Select(c => c.ToDto()).ToList();

    public async Task<ConnectionDto> CreateAsync(ConnectionInput input, CancellationToken ct)
    {
        var c = new GitConnection(input.Name, input.Type, input.Url, clock.UtcNow);
        c.Update(input.Name, input.Type, input.Url, input.Username, Protect(input.Token));
        db.Connections.Add(c);
        await db.SaveChangesAsync(ct);
        return c.ToDto();
    }

    public async Task<ConnectionDto> UpdateAsync(Guid id, ConnectionInput input, CancellationToken ct)
    {
        var c = await db.Connections.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Connection");
        c.Update(input.Name, input.Type, input.Url, input.Username, Protect(input.Token));
        await db.SaveChangesAsync(ct);
        return c.ToDto();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Connections.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Connection");
        if (await db.Pipelines.AnyAsync(p => p.ConnectionId == id, ct))
            throw new DomainException("Pipelines still use this connection.");
        db.Connections.Remove(c);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<RepositoryDto>> RepositoriesAsync(Guid id, CancellationToken ct)
    {
        var c = await db.Connections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Connection");
        if (c.Type != ConnectionType.AzureDevOps) throw new DomainException("Repository listing is only available for Azure DevOps connections.");
        if (c.TokenProtected is null) throw new DomainException("The connection has no personal access token.");
        return await azureDevOps.ListRepositoriesAsync(c.Url, c.Username, secrets.Unprotect(c.TokenProtected), ct);
    }

    private string? Protect(string? token) => string.IsNullOrWhiteSpace(token) ? null : secrets.Protect(token.Trim());
}
