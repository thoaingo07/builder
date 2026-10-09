using System.Reflection;
using FluentMigrator;

namespace Builder.Migrations;

/// <summary>
/// A migration whose Up and Down are the raw SQL files <c>db/migrations/{version:000000}_{title}.{up|down}.sql</c>,
/// embedded into this assembly. Each runs in its own transaction (FluentMigrator's default).
/// </summary>
public abstract class SqlFileMigration : Migration
{
    internal const string ResourcePrefix = "Builder.Migrations.Sql.";

    public override void Up() => Execute.Sql(ReadFile(FileStem, "up"));

    public override void Down() => Execute.Sql(ReadFile(FileStem, "down"));

    internal string FileStem
    {
        get
        {
            var attribute = GetType().GetCustomAttribute<MigrationAttribute>()
                ?? throw new InvalidOperationException($"{GetType().Name} has no [Migration] attribute.");
            return $"{attribute.Version:000000}_{attribute.Description}";
        }
    }

    internal static string ReadFile(string stem, string direction)
    {
        var name = $"{ResourcePrefix}{stem}.{direction}.sql";
        using var stream = typeof(SqlFileMigration).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"db/migrations/{stem}.{direction}.sql is not embedded. Every migration is a pair of files and one line in Migrations.cs.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
