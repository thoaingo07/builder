using System.Security.Cryptography;
using System.Text;
using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Organizations;
using Builder.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Organizations, memberships and per-organization agent tokens.</summary>
public sealed class OrganizationService(IAppDbContext db, IClock clock, ICurrentOrg current)
{
    public async Task<MeDto> MeAsync(string userName, CancellationToken ct)
    {
        var user = await UserAsync(userName, ct);
        return new MeDto(new UserDto(user.UserName, user.DisplayName, user.IsAdmin, user.Email), await OrgsOfAsync(user.Id, ct));
    }

    /// <summary>The caller's role in an organization, or null when not a member.</summary>
    public async Task<(Guid UserId, OrgRole? Role)> MembershipAsync(string userName, Guid orgId, CancellationToken ct)
    {
        var user = await UserAsync(userName, ct);
        var role = await db.Memberships.Where(m => m.OrgId == orgId && m.UserId == user.Id)
            .Select(m => (OrgRole?)m.Role).FirstOrDefaultAsync(ct);
        return (user.Id, role);
    }

    /// <summary>Any signed-in user may create an organization and becomes its owner.</summary>
    public async Task<OrgCreatedDto> CreateAsync(string userName, OrgInput input, CancellationToken ct)
    {
        var user = await UserAsync(userName, ct);
        var org = new Organization(input.Name, user.UserName, clock.UtcNow);
        var slug = org.Slug;
        for (var i = 2; await db.Organizations.AnyAsync(o => o.Slug == slug, ct); i++) slug = $"{org.Slug}-{i}";
        org.UseSlug(slug);
        var token = NewAgentToken();
        org.SetAgentTokenHash(HashToken(token));
        db.Organizations.Add(org);
        db.Memberships.Add(new Membership(org.Id, user.Id, OrgRole.Owner, clock.UtcNow));
        await db.SaveChangesAsync(ct);
        return new OrgCreatedDto((await OrgsOfAsync(user.Id, ct)).First(o => o.Id == org.Id), token);
    }

    public async Task<OrgDto> RenameAsync(OrgInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var org = await db.Organizations.FirstAsync(o => o.Id == current.RequireOrgId(), ct);
        org.Rename(input.Name);
        await db.SaveChangesAsync(ct);
        return (await OrgsOfAsync(current.UserId!.Value, ct)).First(o => o.Id == org.Id);
    }

    /// <summary>Issues a new agent token for the organization; the old one stops working for new connections.</summary>
    public async Task<AgentTokenDto> RegenerateAgentTokenAsync(CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var org = await db.Organizations.FirstAsync(o => o.Id == current.RequireOrgId(), ct);
        var token = NewAgentToken();
        org.SetAgentTokenHash(HashToken(token));
        await db.SaveChangesAsync(ct);
        return new AgentTokenDto(token);
    }

    public async Task<List<MemberDto>> MembersAsync(CancellationToken ct)
    {
        var orgId = current.RequireOrgId();
        return await db.Memberships.Where(m => m.OrgId == orgId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderBy(x => x.u.UserName)
            .Select(x => new MemberDto(x.u.Id, x.u.UserName, x.u.DisplayName, x.u.Email, x.m.Role, x.u.Email != null, x.m.CreatedAt))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Adds someone by e-mail. Unknown e-mails get a user that can sign in with Google (that is the invitation);
    /// only owners can add owners.
    /// </summary>
    public async Task<MemberDto> AddMemberAsync(AddMemberInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        if (input.Role == OrgRole.Owner) current.RequireRole(OrgRole.Owner);
        var orgId = current.RequireOrgId();
        var email = User.NormalizeEmail(input.Email) ?? throw new DomainException("E-mail is required.");
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email || u.UserName == email, ct);
        if (user is null)
        {
            user = User.External(email, null, false, clock.UtcNow);
            db.Users.Add(user);
        }
        if (await db.Memberships.AnyAsync(m => m.OrgId == orgId && m.UserId == user.Id, ct))
            throw new DomainException($"{email} is already a member.");
        db.Memberships.Add(new Membership(orgId, user.Id, input.Role, clock.UtcNow));
        await db.SaveChangesAsync(ct);
        return (await MembersAsync(ct)).First(m => m.UserId == user.Id);
    }

    public async Task<MemberDto> ChangeRoleAsync(Guid userId, ChangeRoleInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var membership = await MemberAsync(userId, ct);
        if (membership.Role == OrgRole.Owner || input.Role == OrgRole.Owner) current.RequireRole(OrgRole.Owner);
        if (membership.Role == OrgRole.Owner && input.Role != OrgRole.Owner) await EnsureAnotherOwnerAsync(userId, ct);
        membership.ChangeRole(input.Role);
        await db.SaveChangesAsync(ct);
        return (await MembersAsync(ct)).First(m => m.UserId == userId);
    }

    /// <summary>Removes a member (admins), or leaves the organization (anyone, for themselves).</summary>
    public async Task RemoveMemberAsync(Guid userId, CancellationToken ct)
    {
        if (userId != current.UserId) current.RequireRole(OrgRole.Admin);
        var membership = await MemberAsync(userId, ct);
        if (membership.Role == OrgRole.Owner)
        {
            if (userId != current.UserId) current.RequireRole(OrgRole.Owner);
            await EnsureAnotherOwnerAsync(userId, ct);
        }
        db.Memberships.Remove(membership);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Resolves an agent registration token to its organization (null = not an org token).</summary>
    public async Task<Guid?> OrgOfAgentTokenAsync(string token, CancellationToken ct)
    {
        var hash = HashToken(token);
        return await db.Organizations.Where(o => o.AgentTokenHash == hash).Select(o => (Guid?)o.Id).FirstOrDefaultAsync(ct);
    }

    public static string HashToken(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string NewAgentToken() => "bldr_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(30))
        .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private async Task<List<OrgDto>> OrgsOfAsync(Guid userId, CancellationToken ct) =>
        await db.Memberships.Where(m => m.UserId == userId)
            .Join(db.Organizations, m => m.OrgId, o => o.Id, (m, o) => new { m, o })
            .OrderBy(x => x.o.Name)
            .Select(x => new OrgDto(x.o.Id, x.o.Name, x.o.Slug, x.m.Role, db.Memberships.Count(c => c.OrgId == x.o.Id), x.o.CreatedAt))
            .ToListAsync(ct);

    private async Task<User> UserAsync(string userName, CancellationToken ct) =>
        await db.Users.FirstOrDefaultAsync(u => u.UserName == userName, ct) ?? throw new NotFoundException("User");

    private async Task<Membership> MemberAsync(Guid userId, CancellationToken ct)
    {
        var orgId = current.RequireOrgId();
        return await db.Memberships.FirstOrDefaultAsync(m => m.OrgId == orgId && m.UserId == userId, ct)
            ?? throw new NotFoundException("Member");
    }

    private async Task EnsureAnotherOwnerAsync(Guid userId, CancellationToken ct)
    {
        var orgId = current.RequireOrgId();
        if (!await db.Memberships.AnyAsync(m => m.OrgId == orgId && m.Role == OrgRole.Owner && m.UserId != userId, ct))
            throw new DomainException("An organization needs at least one owner.");
    }
}
