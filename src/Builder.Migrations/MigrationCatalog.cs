using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentMigrator;

namespace Builder.Migrations;

/// <summary>
/// The chain as this assembly carries it, checked before anything runs: every embedded <c>.up.sql</c>
/// has its <c>.down.sql</c> and exactly one class in <see cref="M000001"/>'s file with the same version
/// and title, and every class has its files.
/// </summary>
public static partial class MigrationCatalog
{
    private static readonly Lazy<IReadOnlyList<(long Version, string Title)>> Chain = new(Load);

    public static IReadOnlyList<(long Version, string Title)> Migrations => Chain.Value;

    public static void Verify() => _ = Chain.Value;

    private static IReadOnlyList<(long Version, string Title)> Load()
    {
        var assembly = typeof(SqlFileMigration).Assembly;
        var files = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(SqlFileMigration.ResourcePrefix, StringComparison.Ordinal))
            .Select(n => n[SqlFileMigration.ResourcePrefix.Length..])
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();
        var chain = new List<(long Version, string Title)>();

        foreach (var type in assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(SqlFileMigration)) && !t.IsAbstract))
        {
            if (type.GetCustomAttribute<MigrationAttribute>() is not { } attribute)
            {
                problems.Add($"{type.Name} has no [Migration] attribute.");
                continue;
            }
            var stem = $"{attribute.Version:000000}_{attribute.Description}";
            foreach (var direction in new[] { "up", "down" })
                if (!files.Contains($"{stem}.{direction}.sql"))
                    problems.Add($"{type.Name} names db/migrations/{stem}.{direction}.sql, which is not there.");
            chain.Add((attribute.Version, attribute.Description));
        }

        foreach (var file in files)
        {
            var match = FileName().Match(file);
            if (!match.Success)
            {
                problems.Add($"db/migrations/{file} is not named {{version:000000}}_{{title}}.{{up|down}}.sql.");
                continue;
            }
            var version = long.Parse(match.Groups["version"].Value, CultureInfo.InvariantCulture);
            if (!chain.Contains((version, match.Groups["title"].Value)))
                problems.Add($"db/migrations/{file} has no line in Migrations.cs.");
        }

        foreach (var duplicate in chain.GroupBy(m => m.Version).Where(g => g.Count() > 1))
            problems.Add($"Version {duplicate.Key} is claimed by {duplicate.Count()} migrations.");

        if (problems.Count > 0)
            throw new InvalidOperationException("The migration chain is inconsistent:\n  " + string.Join("\n  ", problems));
        return chain.OrderBy(m => m.Version).ToList();
    }

    [GeneratedRegex(@"^(?<version>\d{6})_(?<title>[a-z0-9_]+)\.(up|down)\.sql$")]
    private static partial Regex FileName();
}
