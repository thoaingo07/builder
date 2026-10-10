using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain.Builds;
using Builder.Domain.Deployments;
using Builder.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

public sealed class CleanupService(IAppDbContext db, IClock clock, ICurrentOrg current, BuildService builds, AgentService agents)
{
    /// <summary>Deletes the current organization's old builds; workspace/docker cleanup goes to its own agents only.</summary>
    public async Task<CleanupResultDto> RunAsync(CleanupInput input, CancellationToken ct)
    {
        current.RequireRole(Domain.Organizations.OrgRole.Admin);
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
            foreach (var agent in (await agents.ListAsync(ct)).Where(a => a.Online && !a.Shared))
                if (await agents.CleanupAgentAsync(agent.Id, new AgentCleanupInput(input.RemoveWorkspaces, input.DockerPrune), ct))
                    notified++;

        return new CleanupResultDto(doomed.Count, artifacts, bytes, notified);
    }
}

public sealed class DashboardService(IAppDbContext db, IClock clock, AgentService agents, DeploymentService deployments)
{
    /// <summary>The organization's activity, or one project's (agents are always the organization's).</summary>
    public async Task<DashboardDto> GetAsync(CancellationToken ct, Guid? projectId = null)
    {
        var builds = db.Builds.AsNoTracking().Where(b => projectId == null || b.ProjectId == projectId);
        var names = await db.Pipelines.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        var active = await builds.Include(b => b.Jobs)
            .Where(b => b.FinishedAt == null).OrderByDescending(b => b.QueuedAt).Take(50).ToListAsync(ct);
        var recent = await builds.Include(b => b.Jobs)
            .Where(b => b.FinishedAt != null).OrderByDescending(b => b.FinishedAt).Take(15).ToListAsync(ct);

        var since = clock.UtcNow.AddHours(-24);
        var stats = await builds.Where(b => b.QueuedAt >= since)
            .GroupBy(b => b.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new DashboardDto(
            await agents.ListAsync(ct),
            active.Select(b => b.ToSummary(names.GetValueOrDefault(b.PipelineId, "?"))).ToList(),
            recent.Select(b => b.ToSummary(names.GetValueOrDefault(b.PipelineId, "?"))).ToList(),
            await deployments.ListAsync(null, true, ct, projectId),
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

    public const int MinPasswordLength = 12;

    /// <summary>Changes the caller's own password (users who only sign in with Google have none).</summary>
    public async Task ChangePasswordAsync(string userName, ChangePasswordInput input, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.UserName == userName, ct) ?? throw new NotFoundException("User");
        if (user.PasswordHash is null) throw new Domain.DomainException("This account signs in with Google; it has no password.");
        if (!hasher.Verify(user.PasswordHash, input.CurrentPassword)) throw new Domain.DomainException("The current password is wrong.");
        if (input.NewPassword.Length < MinPasswordLength)
            throw new Domain.DomainException($"Use at least {MinPasswordLength} characters.");
        if (input.NewPassword == input.CurrentPassword) throw new Domain.DomainException("The new password must be different.");
        user.SetPasswordHash(hasher.Hash(input.NewPassword));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>True while the bootstrap admin can still sign in with the given (default) password.</summary>
    public async Task<bool> UsesPasswordAsync(string userName, string password, CancellationToken ct)
    {
        var hash = await db.Users.Where(u => u.UserName == userName).Select(u => u.PasswordHash).FirstOrDefaultAsync(ct);
        return hash is not null && hasher.Verify(hash, password);
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
