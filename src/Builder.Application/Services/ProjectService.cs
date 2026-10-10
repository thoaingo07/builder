using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Organizations;
using Builder.Domain.Projects;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Projects of the current organization.</summary>
public sealed class ProjectService(IAppDbContext db, IClock clock, ICurrentOrg current)
{
    public const string DefaultName = "Default";

    public async Task<List<ProjectDto>> ListAsync(CancellationToken ct) =>
        await db.Projects.AsNoTracking().OrderBy(p => p.Name)
            .Select(p => new ProjectDto(p.Id, p.Name, p.Slug, p.Description,
                db.Repositories.Count(r => r.ProjectId == p.Id),
                db.Pipelines.Count(r => r.ProjectId == p.Id),
                db.Connections.Count(c => c.ProjectId == p.Id),
                db.Environments.Count(e => e.ProjectId == p.Id),
                db.Secrets.Count(s => s.ProjectId == p.Id),
                p.CreatedAt))
            .ToListAsync(ct);

    public async Task<ProjectDto> GetAsync(Guid id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(p => p.Id == id) ?? throw new NotFoundException("Project");

    public async Task<ProjectDto> CreateAsync(ProjectInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var project = await AddAsync(input.Name, input.Description, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(project.Id, ct);
    }

    public async Task<ProjectDto> UpdateAsync(Guid id, ProjectInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project");
        await EnsureFreeNameAsync(input.Name, id, ct);
        project.Update(input.Name, input.Description);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Only an empty project can be deleted (move or delete its repositories and settings first).</summary>
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw new NotFoundException("Project");
        var p = await GetAsync(id, ct);
        if (p.RepositoryCount + p.ConnectionCount + p.EnvironmentCount + p.SecretCount > 0)
            throw new DomainException("Move or delete the project's repositories, connections, environments and secrets first.");
        if (await db.Builds.AnyAsync(b => b.ProjectId == id, ct) || await db.Deployments.AnyAsync(d => d.ProjectId == id, ct))
            throw new DomainException("The project still has builds or deployments.");
        db.Projects.Remove(project);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Null (shared by the organization) or a project of the current organization.</summary>
    public async Task<Guid?> CheckAsync(Guid? projectId, CancellationToken ct)
    {
        if (projectId is { } id && !await db.Projects.AnyAsync(p => p.Id == id, ct)) throw new NotFoundException("Project");
        return projectId;
    }

    /// <summary>
    /// The project a new repository goes to: the one given, else the organization's only project (one named
    /// Default is created when there is none yet).
    /// </summary>
    public async Task<Guid> ForRepositoryAsync(Guid? projectId, CancellationToken ct)
    {
        if (projectId is not null) return (await CheckAsync(projectId, ct))!.Value;
        var ids = await db.Projects.Select(p => p.Id).Take(2).ToListAsync(ct);
        return ids.Count switch
        {
            1 => ids[0],
            0 => (await AddAsync(DefaultName, null, ct)).Id,
            _ => throw new DomainException("Pick the project the repository belongs to."),
        };
    }

    private async Task<Project> AddAsync(string name, string? description, CancellationToken ct)
    {
        var project = new Project(current.RequireOrgId(), name, description, clock.UtcNow);
        await EnsureFreeNameAsync(project.Name, null, ct);
        var slug = project.Slug;
        for (var i = 2; await db.Projects.AnyAsync(p => p.Slug == slug, ct); i++) slug = $"{project.Slug}-{i}";
        project.UseSlug(slug);
        db.Projects.Add(project);
        return project;
    }

    private async Task EnsureFreeNameAsync(string name, Guid? except, CancellationToken ct)
    {
        var n = name.Trim().ToLower();
        if (await db.Projects.AnyAsync(p => p.Id != except && p.Name.ToLower() == n, ct))
            throw new DomainException($"A project named '{name.Trim()}' already exists.");
    }
}
