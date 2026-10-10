using System.Text.Json;
using Builder.Application.Abstractions;
using Builder.Domain.Agents;
using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Builder.Domain.Deployments;
using Builder.Domain.Organizations;
using Builder.Domain.Pipelines;
using Builder.Domain.Repositories;
using Builder.Domain.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Builder.Infrastructure.Persistence;

/// <summary>
/// EF Core is the data-access layer only. The schema is owned by the raw SQL migrations in
/// <c>db/migrations</c> (FluentMigrator); keep this mapping in step with them (snake_case names).
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentOrg? currentOrg = null) : DbContext(options), IAppDbContext
{
    /// <summary>
    /// Organization the global query filters scope to. Null (no request, e.g. scheduler/agents) disables them;
    /// read per query, so EF parameterizes it.
    /// </summary>
    private Guid? OrgFilter => currentOrg?.OrgId;

    public DbSet<Pipeline> Pipelines => Set<Pipeline>();
    public DbSet<Build> Builds => Set<Build>();
    public DbSet<BuildJob> BuildJobs => Set<BuildJob>();
    public DbSet<LogLine> LogLines => Set<LogLine>();
    public DbSet<Artifact> Artifacts => Set<Artifact>();
    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<DeployEnvironment> Environments => Set<DeployEnvironment>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<GitConnection> Connections => Set<GitConnection>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<Membership> Memberships => Set<Membership>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Domain.Secrets.Secret> Secrets => Set<Domain.Secrets.Secret>();
    public DbSet<Domain.Triggers.RunnerSchedule> RunnerSchedules => Set<Domain.Triggers.RunnerSchedule>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        b.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Pipeline>(e =>
        {
            e.ToTable("pipelines");
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.LastBuildNumber).IsConcurrencyToken();
        });

        m.Entity<Build>(e =>
        {
            e.ToTable("builds");
            e.Property(x => x.Variables).HasJsonConversion();
            e.HasMany(x => x.Jobs).WithOne().HasForeignKey(j => j.BuildId).OnDelete(DeleteBehavior.Cascade);
            e.Navigation(x => x.Jobs).UsePropertyAccessMode(PropertyAccessMode.Property);
        });

        m.Entity<BuildJob>(e =>
        {
            e.ToTable("build_jobs");
            e.Property(x => x.Order).HasColumnName("order");
            e.Property(x => x.TaskVars).HasJsonConversion();
            e.Property(x => x.Approval).HasJsonConversion();
            e.Property(x => x.Deploy).HasJsonConversion();
            e.Property(x => x.Registries).HasJsonConversion();
            e.Property(x => x.Steps).HasJsonConversion();
        });

        m.Entity<LogLine>(e => e.ToTable("log_lines"));

        m.Entity<Artifact>(e => e.ToTable("artifacts"));

        m.Entity<Agent>(e => e.ToTable("agents"));

        m.Entity<DeployEnvironment>(e => e.ToTable("environments"));

        m.Entity<Deployment>(e => e.ToTable("deployments"));

        m.Entity<GitConnection>(e => e.ToTable("connections"));
        m.Entity<Deployment>().Property(x => x.Container).HasJsonConversion();

        m.Entity<User>(e => e.ToTable("users"));

        m.Entity<Organization>(e => e.ToTable("organizations"));
        m.Entity<Membership>(e =>
        {
            e.ToTable("memberships");
            e.HasKey(x => new { x.OrgId, x.UserId });
        });
        m.Entity<Repository>(e => e.ToTable("repositories"));
        m.Entity<Domain.Secrets.Secret>(e => e.ToTable("secrets"));
        m.Entity<Domain.Triggers.RunnerSchedule>(e =>
        {
            e.ToTable("runner_schedules");
            e.Property(x => x.Vars).HasJsonConversion();
        });
        m.Entity<Pipeline>().Property(x => x.Triggers).HasJsonConversion();

        // organization isolation: every org-scoped table is filtered to the current organization
        m.Entity<Pipeline>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<Build>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<GitConnection>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<DeployEnvironment>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<Deployment>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<Repository>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<Domain.Triggers.RunnerSchedule>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<Domain.Secrets.Secret>().HasQueryFilter(x => OrgFilter == null || x.OrgId == OrgFilter);
        m.Entity<Agent>().HasQueryFilter(x => OrgFilter == null || x.OrgId == null || x.OrgId == OrgFilter);

        // relationships without navigations, mirroring the FKs in db/migrations (also orders inserts)
        m.Entity<Repository>().HasOne<GitConnection>().WithMany().HasForeignKey(r => r.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<Pipeline>().HasOne<Repository>().WithMany().HasForeignKey(p => p.RepositoryId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Repository>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Pipeline>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Build>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<GitConnection>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<DeployEnvironment>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Domain.Triggers.RunnerSchedule>().HasOne<Pipeline>().WithMany().HasForeignKey(x => x.PipelineId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Domain.Triggers.RunnerSchedule>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Domain.Secrets.Secret>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Agent>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Membership>().HasOne<Organization>().WithMany().HasForeignKey(x => x.OrgId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Membership>().HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Build>().HasOne<Pipeline>().WithMany().HasForeignKey(b => b.PipelineId).OnDelete(DeleteBehavior.Restrict);
        m.Entity<LogLine>().HasOne<Build>().WithMany().HasForeignKey(l => l.BuildId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<LogLine>().HasOne<BuildJob>().WithMany().HasForeignKey(l => l.JobId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Artifact>().HasOne<Build>().WithMany().HasForeignKey(a => a.BuildId).OnDelete(DeleteBehavior.Cascade);
        m.Entity<Artifact>().HasOne<BuildJob>().WithMany().HasForeignKey(a => a.JobId).OnDelete(DeleteBehavior.Cascade);

        // Ids are assigned by the domain (Guid v7). Without this EF would treat a new child added to a
        // tracked aggregate (e.g. a job added to a build) as an existing row and issue an UPDATE.
        foreach (var key in m.Model.GetEntityTypes().Select(t => t.FindPrimaryKey()).OfType<Microsoft.EntityFrameworkCore.Metadata.IMutableKey>())
            foreach (var p in key.Properties.Where(p => p.ClrType == typeof(Guid)))
                p.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
    }
}

internal static class JsonColumnExtensions
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Stores a value as jsonb, compared by its serialized form.</summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> builder)
    {
        builder.HasConversion(
                v => JsonSerializer.Serialize(v, Options),
                v => JsonSerializer.Deserialize<T>(v, Options)!,
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                    v => JsonSerializer.Serialize(v, Options).GetHashCode(),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!))
            .HasColumnType("jsonb");
        return builder;
    }
}
