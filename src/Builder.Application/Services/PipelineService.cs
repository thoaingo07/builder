using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain.Organizations;
using Builder.Domain.Pipelines;
using Builder.Domain.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Runners of the current organization (created by mapping runner files, see <see cref="RepositoryService"/>).</summary>
public sealed class PipelineService(
    IAppDbContext db,
    ICurrentOrg current,
    IGitService git,
    ITaskfilePlanner planner,
    GitRemotes remotes)
{
    public async Task<List<PipelineDto>> ListAsync(CancellationToken ct, Guid? projectId = null)
    {
        var pipelines = await db.Pipelines.AsNoTracking().Where(p => projectId == null || p.ProjectId == projectId)
            .OrderBy(p => p.Name).ToListAsync(ct);
        var repos = await db.Repositories.AsNoTracking().ToDictionaryAsync(r => r.Id, ct);
        var lastIds = await db.Builds.GroupBy(b => b.PipelineId)
            .Select(g => g.OrderByDescending(b => b.QueuedAt).Select(b => b.Id).First()).ToListAsync(ct);
        var last = await db.Builds.AsNoTracking().Include(b => b.Jobs).Where(b => lastIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.PipelineId, ct);
        return pipelines.Where(p => repos.ContainsKey(p.RepositoryId))
            .Select(p => ToDto(p, repos[p.RepositoryId], last.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<PipelineDto> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        var repo = await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == p.RepositoryId, ct);
        var last = await db.Builds.AsNoTracking().Include(b => b.Jobs).Where(b => b.PipelineId == id)
            .OrderByDescending(b => b.QueuedAt).FirstOrDefaultAsync(ct);
        return ToDto(p, repo, last);
    }

    public async Task<PipelineDto> UpdateAsync(Guid id, PipelineInput input, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Runner");
        if (await db.Pipelines.AnyAsync(x => x.Id != id && x.Name == input.Name.Trim(), ct))
            throw new Domain.DomainException($"A runner named '{input.Name}' already exists.");
        p.Update(input.Name, input.EntryTask);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Unmaps a runner (the file stays in the repository) and deletes its builds.</summary>
    public async Task DeleteAsync(Guid id, BuildService builds, CancellationToken ct)
    {
        current.RequireRole(OrgRole.Admin);
        var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Runner");
        if (await db.Builds.AnyAsync(b => b.PipelineId == id && b.FinishedAt == null, ct))
            throw new Domain.DomainException("The runner has builds in progress.");
        var buildIds = await db.Builds.Where(b => b.PipelineId == id).Select(b => b.Id).ToListAsync(ct);
        await builds.DeleteBuildsAsync(buildIds, ct);
        db.Pipelines.Remove(p);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TaskfileDto> GetTaskfileAsync(Guid id, string? branch, CancellationToken ct)
    {
        var (p, repo) = await WithRepositoryAsync(id, ct);
        var remote = await remotes.ForRepositoryAsync(repo, ct);
        var b = string.IsNullOrWhiteSpace(branch) ? repo.DefaultBranch : branch.Trim();
        var commit = await git.ResolveBranchAsync(remote, b, ct);
        var content = await git.ReadFileAsync(remote, b, commit, p.TaskfilePath, ct)
            ?? throw new NotFoundException($"'{p.TaskfilePath}' on branch '{b}'");
        return new TaskfileDto(p.TaskfilePath, b, commit, content);
    }

    /// <summary>What the Run dialog must ask for: requires.vars of every task the runner's entry reaches, at that branch.</summary>
    public async Task<RunInputsDto> InputsAsync(Guid id, string? branch, string? entryTask, CancellationToken ct)
    {
        var (p, _) = await WithRepositoryAsync(id, ct);
        var taskfile = await GetTaskfileAsync(id, branch, ct);
        var plan = planner.Plan(taskfile.Content, string.IsNullOrWhiteSpace(entryTask) ? p.EntryTask : entryTask);
        return new RunInputsDto(taskfile.Branch, plan.EntryTask,
            plan.Inputs.Select(i => new RunInputDto(i.Input.Name, i.Input.Enum, i.RequiredBy.ToList())).ToList());
    }

    public PlanPreviewDto Preview(PlanRequest request)
    {
        try
        {
            var plan = planner.Plan(request.Content, request.EntryTask);
            return new PlanPreviewDto(plan.EntryTask,
                plan.Jobs.Select(j => new PlanJobDto(j.Key, j.TaskName, j.DependsOn.ToList(), j.Approval is not null, j.Deploy?.Environment)).ToList(),
                null);
        }
        catch (TaskfileException ex)
        {
            return new PlanPreviewDto(request.EntryTask ?? "", [], ex.Message);
        }
    }

    private async Task<Pipeline> FindAsync(Guid id, CancellationToken ct) =>
        await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Runner");

    private async Task<(Pipeline, Repository)> WithRepositoryAsync(Guid id, CancellationToken ct)
    {
        var p = await FindAsync(id, ct);
        return (p, await db.Repositories.AsNoTracking().FirstAsync(r => r.Id == p.RepositoryId, ct));
    }

    private static PipelineDto ToDto(Pipeline p, Repository repo, Domain.Builds.Build? last) => new(
        p.Id, p.Name, repo.Id, repo.Name, repo.Url, repo.DefaultBranch, p.TaskfilePath, p.EntryTask, last?.ToSummary(p.Name), p.ProjectId);
}
