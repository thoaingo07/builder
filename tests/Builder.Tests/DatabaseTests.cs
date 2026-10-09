using Builder.Domain.Agents;
using Builder.Domain.Builds;
using Builder.Domain.Connections;
using Builder.Domain.Deployments;
using Builder.Domain.Pipelines;
using Builder.Domain.Users;
using Builder.Infrastructure.Persistence;
using Builder.Migrations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Builder.Tests;

/// <summary>
/// Runs the raw SQL migrations on a fresh database (on the Aspire-managed PostgreSQL) and checks EF maps onto the result.
/// Each test gets its own database so it never touches the one the running API uses.
/// </summary>
[Collection(AspireCollection.Name)]
public sealed class DatabaseTests(AspireFixture aspire) : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        _connectionString = new NpgsqlConnectionStringBuilder(aspire.ConnectionString)
        {
            Database = "test_" + Guid.NewGuid().ToString("N")[..12],
        }.ConnectionString;
        using var migrator = new DatabaseMigrator(_connectionString);
        await migrator.EnsureDatabaseAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(_connectionString).UseSnakeCaseNamingConvention().Options);

    [Fact]
    public async Task Migrations_go_up_down_and_up_again()
    {
        using var migrator = new DatabaseMigrator(_connectionString);
        migrator.Up();
        Assert.All(migrator.Status(), m => Assert.True(m.Applied));
        Assert.Contains("build_jobs", await TablesAsync());

        migrator.DownTo(0);
        Assert.All(migrator.Status(), m => Assert.False(m.Applied));
        Assert.Equal(["VersionInfo"], await TablesAsync());

        migrator.Up();
        Assert.All(migrator.Status(), m => Assert.True(m.Applied));
    }

    [Fact]
    public async Task Ef_model_matches_the_sql_schema()
    {
        using (var migrator = new DatabaseMigrator(_connectionString)) migrator.Up();

        // every mapped column exists in the schema
        await using var db = Db();
        var columns = await ColumnsAsync();
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, null))!;
                Assert.True(columns.Contains((table, column)), $"{entity.ClrType.Name}.{property.Name} → {table}.{column} is missing in db/migrations");
            }
        }

        // and a full aggregate round-trips
        var connection = new GitConnection("azdo", ConnectionType.AzureDevOps, "https://dev.azure.com/org", Now);
        var pipeline = new Pipeline("shop", connection.Id, "https://dev.azure.com/org/p/_git/shop", "main", "Taskfile.yml", "ci", Now);
        var build = Build.Queue(pipeline.Id, pipeline.NextBuildNumber(), "main", null, new() { ["X"] = "1" }, "admin", Now);
        db.AddRange(connection, pipeline, build, new Agent("agent-1", Now), new User("admin", "Admin", "hash", true, Now),
            new DeployEnvironment("prod", EnvironmentType.SshDocker, Now));
        await db.SaveChangesAsync();

        build.Planned("abc123", "ci", [
            new BuildJob("a", "a", "first", 0, [], new() { ["OS"] = "linux" }, ["linux"], ["out/**"], true, null, null),
            new BuildJob("b", "b", null, 1, ["a"], null, null, null, true,
                new ApprovalSpec("ok?", ["alice"]), new DeploySpec("prod", "c.yml", "shop", null, null, "https://x")),
        ], Now);
        await db.SaveChangesAsync();
        db.LogLines.Add(new LogLine(build.Id, build.Jobs[0].Id, Now, LogStreamKind.Out, "hello"));
        await db.SaveChangesAsync();

        await using var fresh = Db();
        var loaded = await fresh.Builds.Include(b => b.Jobs).SingleAsync(b => b.Id == build.Id);
        Assert.Equal(BuildStatus.Running, loaded.Status);
        Assert.Equal("1", loaded.Variables["X"]);
        var b = loaded.Jobs.Single(j => j.Key == "b");
        Assert.Equal(["a"], b.DependsOn);
        Assert.Equal(["alice"], b.Approval!.Approvers);
        Assert.Equal("shop", b.Deploy!.Project);
        Assert.Equal("linux", loaded.Jobs.Single(j => j.Key == "a").TaskVars["OS"]);
        Assert.Equal(1, await fresh.LogLines.CountAsync());
    }

    private async Task<List<string>> TablesAsync()
    {
        await using var c = new NpgsqlConnection(_connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT table_name FROM information_schema.tables WHERE table_schema='public' ORDER BY 1", c);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) list.Add(r.GetString(0));
        return list;
    }

    private async Task<HashSet<(string, string)>> ColumnsAsync()
    {
        await using var c = new NpgsqlConnection(_connectionString);
        await c.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT table_name, column_name FROM information_schema.columns WHERE table_schema='public'", c);
        var set = new HashSet<(string, string)>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) set.Add((r.GetString(0), r.GetString(1)));
        return set;
    }
}
