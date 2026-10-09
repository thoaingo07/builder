using Builder.Application.Abstractions;
using Builder.Application.Dtos;
using Builder.Domain.Builds;
using Microsoft.EntityFrameworkCore;

namespace Builder.Application.Services;

/// <summary>Pushes build/job changes to the UI after they are saved.</summary>
public sealed class BuildEvents(IAppDbContext db, IUiNotifier ui)
{
    public async Task PublishAsync(Build build, IEnumerable<BuildJob> changedJobs, CancellationToken ct = default)
    {
        var pipelineName = await db.Pipelines.AsNoTracking()
            .Where(p => p.Id == build.PipelineId).Select(p => p.Name).FirstOrDefaultAsync(ct) ?? "?";
        foreach (var job in changedJobs.DistinctBy(j => j.Id))
            await ui.JobUpdated(job.ToDto());
        await ui.BuildUpdated(build.ToSummary(pipelineName));
    }
}
