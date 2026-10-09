using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain.Builds;
using Builder.Domain.Deployments;
using Builder.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

public sealed class CleanupService(IAppDbContext db, IClock clock, BuildService builds, AgentService agents)
{
    public async Task<CleanupResultDto> RunAsync(CleanupInput input, CancellationToken ct)
    {
        var cutoff = clock.UtcNow.AddDays(-Math.Max(0, input.OlderThanDays));
        var keep = Math.Max(0, input.KeepLastPerPipeline);

        var finished = await db.Builds.AsNoTracking()
            .Where(b => b.FinishedAt != null)
            .Select(b => new { b.Id, b.PipelineId, b.QueuedAt, b.FinishedAt })
            .ToListAsync(ct);
        var deployed = await db.Deployments.Where(d => d.Status == DeploymentStatus.Active).Select(d => d.BuildId).ToListAsync(ct);

        var doomed = finished
            .GroupBy(b => b.PipelineId)
            .SelectMany(g => g.OrderByDescending(b => b.QueuedAt).Skip(keep))
            .Where(b => b.FinishedAt < cutoff && !deployed.Contains(b.Id))
            .Select(b => b.Id).ToList();

        var (artifacts, bytes) = await builds.DeleteBuildsAsync(doomed, ct);

        var notified = 0;
        if (input.RemoveWorkspaces || input.DockerPrune)
            foreach (var agent in (await agents.ListAsync(ct)).Where(a => a.Online))
                if (await agents.RequestCleanupAsync(agent.Id, new AgentCleanupInput(input.RemoveWorkspaces, input.DockerPrune), ct))
                    notified++;

        return new CleanupResultDto(doomed.Count, artifacts, bytes, notified);
    }
}

public sealed class DashboardService(IAppDbContext db, IClock clock, AgentService agents, DeploymentService deployments)
{
    public async Task<DashboardDto> GetAsync(CancellationToken ct)
    {
        var names = await db.Pipelines.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var active = await db.Builds.AsNoTracking().Include(b => b.Jobs)
            .Where(b => b.FinishedAt == null).OrderByDescending(b => b.QueuedAt).Take(50).ToListAsync(ct);
        var recent = await db.Builds.AsNoTracking().Include(b => b.Jobs)
            .Where(b => b.FinishedAt != null).OrderByDescending(b => b.FinishedAt).Take(15).ToListAsync(ct);

        var since = clock.UtcNow.AddHours(-24);
        var stats = await db.Builds.Where(b => b.QueuedAt >= since)
            .GroupBy(b => b.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new DashboardDto(
            await agents.ListAsync(ct),
            active.Select(b => b.ToSummary(names.GetValueOrDefault(b.PipelineId, "?"))).ToList(),
            recent.Select(b => b.ToSummary(names.GetValueOrDefault(b.PipelineId, "?"))).ToList(),
            await deployments.ListAsync(null, true, ct),
            new Last24hDto(stats.GetValueOrDefault(BuildStatus.Succeeded), stats.GetValueOrDefault(BuildStatus.Failed),
                stats.GetValueOrDefault(BuildStatus.Canceled),
                stats.GetValueOrDefault(BuildStatus.Running) + stats.GetValueOrDefault(BuildStatus.Planning) + stats.GetValueOrDefault(BuildStatus.Canceling)));
    }
}

public sealed class AuthService(IAppDbContext db, IClock clock, IPasswordHasher hasher)
{
    public async Task<UserDto?> ValidateAsync(LoginInput input, CancellationToken ct)
    {
        var name = input.UserName.Trim().ToLowerInvariant();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserName == name, ct);
        return user?.PasswordHash is { } hash && hasher.Verify(hash, input.Password)
            ? new UserDto(user.UserName, user.DisplayName, user.IsAdmin)
            : null;
    }

    /// <summary>
    /// Sign-in through an external provider (Google). Only a verified e-mail that belongs to a predefined
    /// user is accepted; everyone else gets null.
    /// </summary>
    public async Task<UserDto?> ExternalLoginAsync(ExternalLoginInput input, CancellationToken ct)
    {
        if (!input.EmailVerified) return null;
        var email = User.NormalizeEmail(input.Email);
        if (email is null) return null;
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
        return user is null ? null : new UserDto(user.UserName, user.DisplayName, user.IsAdmin);
    }

    /// <summary>Creates the bootstrap admin on first start.</summary>
    public async Task EnsureAdminAsync(string userName, string password, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct)) return;
        db.Users.Add(new User(userName, "Administrator", hasher.Hash(password), true, clock.UtcNow));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Makes sure every predefined external user exists (matched by e-mail) with the configured name and role.
    /// Users are never removed here; take someone off the list and delete them separately.
    /// </summary>
    public async Task EnsureAllowedUsersAsync(IEnumerable<AllowedUser> allowed, CancellationToken ct)
    {
        foreach (var a in allowed)
        {
            var email = User.NormalizeEmail(a.Email);
            if (email is null) continue;
            var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
            if (user is null) db.Users.Add(User.External(email, a.DisplayName, a.IsAdmin, clock.UtcNow));
            else user.UpdateProfile(a.DisplayName, a.IsAdmin);
        }
        await db.SaveChangesAsync(ct);
    }
}
