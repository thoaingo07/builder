using FluentMigrator.Runner;
using FluentMigrator.Runner.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Builder.Migrations;

/// <summary>
/// Applies and reverts the <c>db/migrations</c> chain on a PostgreSQL database. Applied versions are
/// rows in FluentMigrator's <c>public."VersionInfo"</c>. The SQL itself is never logged.
/// Used by the console (<c>task migrate:*</c>), the API at startup and the tests.
/// </summary>
public sealed class DatabaseMigrator : IDisposable
{
    private readonly ServiceProvider _services;
    private readonly IServiceScope _scope;
    private readonly string _connectionString;

    public DatabaseMigrator(string connectionString, ILoggerProvider? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        MigrationCatalog.Verify();
        _connectionString = connectionString;
        _services = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(r => r
                .AddPostgres()
                .WithGlobalConnectionString(connectionString)
                .WithGlobalCommandTimeout(TimeSpan.FromMinutes(10))
                .ScanIn(typeof(SqlFileMigration).Assembly).For.Migrations())
            .AddLogging(l =>
            {
                l.ClearProviders();
                if (logger is not null) l.AddProvider(logger);
            })
            .Configure<FluentMigratorLoggerOptions>(o =>
            {
                o.ShowSql = false;
                o.ShowElapsedTime = true;
            })
            .BuildServiceProvider(validateScopes: false);
        _scope = _services.CreateScope();
    }

    private IMigrationRunner Runner => _scope.ServiceProvider.GetRequiredService<IMigrationRunner>();

    public static IReadOnlyList<(long Version, string Title)> Chain() => MigrationCatalog.Migrations;

    /// <summary>Creates the database named in the connection string when it does not exist.</summary>
    public async Task EnsureDatabaseAsync(CancellationToken ct = default)
    {
        var target = new NpgsqlConnectionStringBuilder(_connectionString);
        var database = target.Database ?? throw new InvalidOperationException("The connection string names no database.");
        var admin = new NpgsqlConnectionStringBuilder(_connectionString) { Database = "postgres", Pooling = false };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(ct);
        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @n", connection);
        exists.Parameters.AddWithValue("n", database);
        if (await exists.ExecuteScalarAsync(ct) is not null) return;
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", connection);
        await create.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Applies every pending migration (or up to <paramref name="version"/>).</summary>
    public void Up(long? version = null)
    {
        if (version is { } v) Runner.MigrateUp(v);
        else Runner.MigrateUp();
    }

    /// <summary>Reverts down to <paramref name="version"/> (0 = everything), running each .down.sql.</summary>
    public void DownTo(long version) => Runner.MigrateDown(version);

    /// <summary>Reverts the newest <paramref name="steps"/> applied migrations.</summary>
    public void Rollback(int steps = 1) => Runner.Rollback(steps);

    /// <summary>Every migration with whether it is applied.</summary>
    public IReadOnlyList<(long Version, string Title, bool Applied)> Status()
    {
        var loader = _scope.ServiceProvider.GetRequiredService<FluentMigrator.Runner.IVersionLoader>();
        loader.LoadVersionInfo();
        return Chain().Select(m => (m.Version, m.Title, loader.VersionInfo.HasAppliedMigration(m.Version))).ToList();
    }

    public void Dispose()
    {
        _scope.Dispose();
        _services.Dispose();
    }
}
