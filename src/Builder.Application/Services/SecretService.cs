using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Builds;
using Builder.Domain.Organizations;
using Builder.Domain.Secrets;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Organization secrets: write-only for people, readable only by the agent running a job that declares them.</summary>
public sealed class SecretService(IAppDbContext db, IClock clock, ICurrentOrg current, ISecretProtector protector)
{
    public async Task<List<SecretDto>> ListAsync(CancellationToken ct) =>
        await db.Secrets.AsNoTracking().OrderBy(s => s.Name)
            .Select(s => new SecretDto(s.Id, s.Name, s.Description, s.UpdatedAt, s.UpdatedBy)).ToListAsync(ct);

    public async Task<SecretDto> CreateAsync(SecretInput input, string user, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        if (string.IsNullOrEmpty(input.Value)) throw new DomainException("A secret needs a value.");
        var secret = new Secret(current.RequireOrgId(), input.Name, protector.Protect(input.Value), input.Description, user, clock.UtcNow);
        if (await db.Secrets.AnyAsync(s => s.Name == secret.Name, ct))
            throw new DomainException($"A secret named '{secret.Name}' already exists.");
        db.Secrets.Add(secret);
        await db.SaveChangesAsync(ct);
        return ToDto(secret);
    }

    /// <summary>Changes the description and, when a value is sent, replaces the value. The name is fixed.</summary>
    public async Task<SecretDto> UpdateAsync(Guid id, SecretInput input, string user, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Secret");
        secret.Update(string.IsNullOrEmpty(input.Value) ? null : protector.Protect(input.Value), input.Description, user, clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return ToDto(secret);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var secret = await db.Secrets.FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw new NotFoundException("Secret");
        db.Secrets.Remove(secret);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Values for a job, for the agent that is running it: the job must be assigned to that agent and still be
    /// running, and only the names the job declared are returned.
    /// </summary>
    public async Task<Dictionary<string, string>> ForJobAsync(Guid agentId, Guid jobId, CancellationToken ct)
    {
        var job = await db.BuildJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null || job.AgentId != agentId || job.Status is not (JobStatus.Assigned or JobStatus.Running))
            throw new ForbiddenException("This agent is not running that job.");
        if (job.Secrets.Count == 0) return new();
        var orgId = await db.Builds.IgnoreQueryFilters().Where(b => b.Id == job.BuildId).Select(b => b.OrgId).FirstAsync(ct);
        var secrets = await db.Secrets.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.OrgId == orgId && job.Secrets.Contains(s.Name)).ToListAsync(ct);
        var missing = job.Secrets.Except(secrets.Select(s => s.Name)).ToList();
        if (missing.Count > 0) throw new DomainException($"Secret(s) {string.Join(", ", missing)} no longer exist.");
        return secrets.ToDictionary(s => s.Name, s => protector.Unprotect(s.ValueProtected));
    }

    private static SecretDto ToDto(Secret s) => new(s.Id, s.Name, s.Description, s.UpdatedAt, s.UpdatedBy);
}
