using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain;
using Builder.Domain.Organizations;
using Builder.Domain.Pipelines;
using Builder.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>
/// Repositories of the current organization, and their runner files: every Taskfile in
/// <c>.builder/runners/</c> can be picked and mapped into a runner that Builder can run on any branch.
/// </summary>
public sealed class RepositoryService(
    IAppDbContext db,
    IClock clock,
    ICurrentOrg current,
    IGitService git,
    ITaskfilePlanner planner,
    GitRemotes remotes,
    ProjectService projects)
{
    public async Task<List<RepositoryDto>> ListAsync(CancellationToken ct, Guid? projectId = null)
    {
        var repos = await db.Repositories.AsNoTracking().Where(r => projectId == null || r.ProjectId == projectId)
            .OrderBy(r => r.Name).ToListAsync(ct);
        var connections = await db.Connections.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var counts = await db.Pipelines.GroupBy(p => p.RepositoryId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return repos.Select(r => ToDto(r, connections, counts.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<RepositoryDto> GetAsync(Guid id, CancellationToken ct) =>
        (await ListAsync(ct)).FirstOrDefault(r => r.Id == id) ?? throw new NotFoundException("Repository");

    public async Task<RepositoryDto> CreateAsync(RepositoryInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var projectId = await projects.ForRepositoryAsync(input.ProjectId, ct);
        await EnsureConnectionAsync(input.ConnectionId, projectId, ct);
        var repo = new Repository(current.RequireOrgId(), projectId, input.ConnectionId, input.Name, input.Url, input.DefaultBranch, clock.UtcNow);
        if (await db.Repositories.AnyAsync(r => r.Url == repo.Url, ct))
            throw new DomainException("This repository was already added.");
        // fail early with a clear message when the URL or credentials are wrong
        await git.ResolveBranchAsync(await remotes.ForRepositoryAsync(repo, ct), repo.DefaultBranch, ct);
        db.Repositories.Add(repo);
        await db.SaveChangesAsync(ct);
        return await GetAsync(repo.Id, ct);
    }

    public async Task<RepositoryDto> UpdateAsync(Guid id, RepositoryInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var repo = await FindAsync(id, ct);
        var projectId = input.ProjectId is null ? repo.ProjectId : (await projects.CheckAsync(input.ProjectId, ct))!.Value;
        await EnsureConnectionAsync(input.ConnectionId, projectId, ct);
        repo.Update(input.ConnectionId, input.Name, input.Url, input.DefaultBranch);
        if (projectId != repo.ProjectId) await MoveAsync(repo, projectId, ct);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, BuildService builds, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var repo = await FindAsync(id, ct);
        var runnerIds = await db.Pipelines.Where(p => p.RepositoryId == id).Select(p => p.Id).ToListAsync(ct);
        if (await db.Builds.AnyAsync(b => runnerIds.Contains(b.PipelineId) && b.FinishedAt == null, ct))
            throw new DomainException("Runners of this repository have builds in progress.");
        await builds.DeleteBuildsAsync(await db.Builds.Where(b => runnerIds.Contains(b.PipelineId)).Select(b => b.Id).ToListAsync(ct), ct);
        db.Repositories.Remove(repo); // runners cascade
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<string>> BranchesAsync(Guid id, CancellationToken ct)
    {
        var repo = await FindAsync(id, ct);
        var branches = await git.ListBranchesAsync(await remotes.ForRepositoryAsync(repo, ct), ct);
        // default branch first, then alphabetical
        return branches.OrderBy(b => b == repo.DefaultBranch ? 0 : 1).ThenBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Lists <c>.builder/runners/*.yml</c> at a branch, with each file's tasks and whether it is mapped.</summary>
    public async Task<RunnerFilesDto> RunnerFilesAsync(Guid id, string? branch, CancellationToken ct)
    {
        var repo = await FindAsync(id, ct);
        var remote = await remotes.ForRepositoryAsync(repo, ct);
        var b = string.IsNullOrWhiteSpace(branch) ? repo.DefaultBranch : branch.Trim();
        var commit = await git.ResolveBranchAsync(remote, b, ct);
        var paths = (await git.ListFilesAsync(remote, b, commit, Repository.RunnersFolder, ct)).Where(Repository.IsRunnerFile).ToList();
        var mapped = await db.Pipelines.AsNoTracking().Where(p => p.RepositoryId == id)
            .ToDictionaryAsync(p => p.TaskfilePath, p => (p.Id, p.Name), ct);

        var files = new List<RunnerFileDto>();
        foreach (var path in paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var yaml = await git.ReadFileAsync(remote, b, commit, path, ct) ?? "";
            string? entry = null, error = null;
            var tasks = new List<string>();
            try
            {
                var plan = planner.Plan(yaml, null);
                entry = plan.EntryTask;
                tasks = plan.Jobs.Select(j => j.Key).ToList();
            }
            catch (TaskfileException ex)
            {
                error = ex.Message;
            }
            var m = mapped.TryGetValue(path, out var x) ? x : ((Guid Id, string Name)?)null;
            files.Add(new RunnerFileDto(path, Pipeline.NameFromFile(path), entry, tasks, error, m?.Id, m?.Name));
        }
        return new RunnerFilesDto(b, commit, files);
    }

    /// <summary>Maps the picked runner files into runners. Already-mapped files are left alone.</summary>
    public async Task<List<PipelineDto>> MapRunnersAsync(Guid id, MapRunnersInput input, PipelineService pipelines, CancellationToken ct,
        TriggerService? triggers = null)
    {
        current.RequireRole(OrgRole.Admin);
        var repo = await FindAsync(id, ct);
        var created = new List<Guid>();
        foreach (var r in input.Runners)
        {
            var path = r.Path.Trim().TrimStart('/');
            if (!Repository.IsRunnerFile(path))
                throw new DomainException($"'{r.Path}' is not a runner file (expected {Repository.RunnersFolder}/<name>.yml).");
            if (await db.Pipelines.AnyAsync(p => p.RepositoryId == id && p.TaskfilePath == path, ct)) continue;
            var name = string.IsNullOrWhiteSpace(r.Name) ? Pipeline.NameFromFile(path) : r.Name.Trim();
            if (await db.Pipelines.AnyAsync(p => p.Name == name && p.ProjectId == repo.ProjectId, ct)
                || db.Pipelines.Local.Any(p => p.Name == name && p.ProjectId == repo.ProjectId))
                name = $"{repo.Name}-{name}";
            var runner = new Pipeline(repo, name, path, r.EntryTask, clock.UtcNow);
            db.Pipelines.Add(runner);
            created.Add(runner.Id);
        }
        await db.SaveChangesAsync(ct);
        // pick up the new runners' schedules right away
        if (triggers is not null)
            foreach (var runnerId in created)
                try { await triggers.RefreshAsync(runnerId, ct); } catch (Exception ex) when (ex is ExternalServiceException or DomainException) { }
        return (await pipelines.ListAsync(ct)).Where(p => created.Contains(p.Id)).ToList();
    }

    private async Task<Repository> FindAsync(Guid id, CancellationToken ct) =>
        await db.Repositories.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new NotFoundException("Repository");

    private async Task EnsureConnectionAsync(Guid? connectionId, Guid projectId, CancellationToken ct)
    {
        if (connectionId is not { } cid) return;
        var c = await db.Connections.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) ?? throw new NotFoundException("Connection");
        if (c.ProjectId is { } owner && owner != projectId)
            throw new DomainException($"Connection '{c.Name}' belongs to another project; share it or use one of this project.");
    }

    /// <summary>Moves a repository with its runners, builds and deployments to another project.</summary>
    private async Task MoveAsync(Repository repo, Guid projectId, CancellationToken ct)
    {
        var runners = await db.Pipelines.Where(p => p.RepositoryId == repo.Id).ToListAsync(ct);
        var names = runners.Select(r => r.Name).ToList();
        var clash = await db.Pipelines.Where(p => p.ProjectId == projectId && names.Contains(p.Name)).Select(p => p.Name).FirstOrDefaultAsync(ct);
        if (clash is not null) throw new DomainException($"The project already has a runner named '{clash}'; rename one first.");
        repo.MoveTo(projectId);
        foreach (var r in runners) r.MovedTo(projectId);
        var ids = runners.Select(r => r.Id).ToList();
        await db.Builds.Where(b => ids.Contains(b.PipelineId)).ExecuteUpdateAsync(u => u.SetProperty(b => b.ProjectId, projectId), ct);
        await db.Deployments.Where(d => ids.Contains(d.PipelineId)).ExecuteUpdateAsync(u => u.SetProperty(d => d.ProjectId, projectId), ct);
    }

    private static RepositoryDto ToDto(Repository r, Dictionary<Guid, string> connections, int runners) => new(
        r.Id, r.Name, r.Url, r.ConnectionId, r.ConnectionId is { } c ? connections.GetValueOrDefault(c) : null,
        r.DefaultBranch, runners, r.CreatedAt, r.ProjectId);
}
