using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain.Pipelines;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

public sealed class PipelineService(
    IAppDbContext db,
    IClock clock,
    IGitService git,
    ITaskfilePlanner planner,
    GitRemotes remotes)
{
    public async Task<List<PipelineDto>> ListAsync(CancellationToken ct)
    {
        var pipelines = await db.Pipelines.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
        var connections = await db.Connections.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var lastIds = await db.Builds.GroupBy(b => b.PipelineId)
            .Select(g => g.OrderByDescending(b => b.QueuedAt).Select(b => b.Id).First()).ToListAsync(ct);
        var last = await db.Builds.AsNoTracking().Include(b => b.Jobs).Where(b => lastIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.PipelineId, ct);
        return pipelines.Select(p => ToDto(p, connections, last.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<PipelineDto> GetAsync(Guid id, CancellationToken ct)
    {
        var p = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Pipeline");
        var connections = await db.Connections.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var last = await db.Builds.AsNoTracking().Include(b => b.Jobs).Where(b => b.PipelineId == id)
            .OrderByDescending(b => b.QueuedAt).FirstOrDefaultAsync(ct);
        return ToDto(p, connections, last);
    }

    public async Task<PipelineDto> CreateAsync(PipelineInput input, CancellationToken ct)
    {
        await EnsureConnectionAsync(input.ConnectionId, ct);
        var p = new Pipeline(input.Name, input.ConnectionId, input.RepositoryUrl, input.DefaultBranch ?? "main",
            input.TaskfilePath ?? "Taskfile.yml", input.EntryTask, clock.UtcNow);
        if (await db.Pipelines.AnyAsync(x => x.Name == p.Name, ct))
            throw new Domain.DomainException($"A pipeline named '{p.Name}' already exists.");
        db.Pipelines.Add(p);
        await db.SaveChangesAsync(ct);
        return await GetAsync(p.Id, ct);
    }

    public async Task<PipelineDto> UpdateAsync(Guid id, PipelineInput input, CancellationToken ct)
    {
        await EnsureConnectionAsync(input.ConnectionId, ct);
        var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Pipeline");
        p.Update(input.Name, input.ConnectionId, input.RepositoryUrl, input.DefaultBranch ?? p.DefaultBranch,
            input.TaskfilePath ?? p.TaskfilePath, input.EntryTask);
        await db.SaveChangesAsync(ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, BuildService builds, CancellationToken ct)
    {
        var p = await db.Pipelines.FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Pipeline");
        if (await db.Builds.AnyAsync(b => b.PipelineId == id && b.FinishedAt == null, ct))
            throw new Domain.DomainException("The pipeline has builds in progress.");
        var buildIds = await db.Builds.Where(b => b.PipelineId == id).Select(b => b.Id).ToListAsync(ct);
        await builds.DeleteBuildsAsync(buildIds, ct);
        db.Pipelines.Remove(p);
        await db.SaveChangesAsync(ct);
    }

    public async Task<TaskfileDto> GetTaskfileAsync(Guid id, string? branch, CancellationToken ct)
    {
        var p = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Pipeline");
        var remote = await remotes.ForPipelineAsync(p, ct);
        var b = string.IsNullOrWhiteSpace(branch) ? p.DefaultBranch : branch.Trim();
        var commit = await git.ResolveBranchAsync(remote, b, ct);
        var content = await git.ReadFileAsync(remote, b, commit, p.TaskfilePath, ct) ?? StarterTaskfile;
        return new TaskfileDto(p.TaskfilePath, b, commit, content);
    }

    public async Task<TaskfileDto> SaveTaskfileAsync(Guid id, SaveTaskfileInput input, string userName, CancellationToken ct)
    {
        var p = await db.Pipelines.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new NotFoundException("Pipeline");
        planner.Plan(input.Content, p.EntryTask); // refuse to commit a Taskfile Builder cannot plan
        var remote = await remotes.ForPipelineAsync(p, ct);
        var b = string.IsNullOrWhiteSpace(input.Branch) ? p.DefaultBranch : input.Branch.Trim();
        var message = string.IsNullOrWhiteSpace(input.Message) ? $"Update {p.TaskfilePath} via Builder" : input.Message.Trim();
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.UserName == userName, ct);
        var commit = await git.CommitFileAsync(remote, b, p.TaskfilePath, input.Content, message,
            user?.DisplayName ?? userName, $"{userName}@builder.local", ct);
        return new TaskfileDto(p.TaskfilePath, b, commit, input.Content);
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

    private async Task EnsureConnectionAsync(Guid? connectionId, CancellationToken ct)
    {
        if (connectionId is { } cid && !await db.Connections.AnyAsync(c => c.Id == cid, ct))
            throw new NotFoundException("Connection");
    }

    private static PipelineDto ToDto(Pipeline p, Dictionary<Guid, string> connections, Domain.Builds.Build? last) => new(
        p.Id, p.Name, p.ConnectionId, p.ConnectionId is { } c ? connections.GetValueOrDefault(c) : null,
        p.RepositoryUrl, p.DefaultBranch, p.TaskfilePath, p.EntryTask, last?.ToSummary(p.Name));

    public const string StarterTaskfile = """
        version: '3'

        x-builder:
          entry: ci

        tasks:
          ci:
            desc: Build and test
            deps: [build]

          build:
            cmds:
              - echo "Hello from Builder"
        """;
}
