using Builder.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

// Usage: Builder.Migrations <up [version] | down <version> | rollback [steps] | status>
// Connection string: BUILDER_DB, or ConnectionStrings:Builder (env ConnectionStrings__builder, as Aspire
// injects it), or the dev default.
var config = new ConfigurationBuilder().AddEnvironmentVariables().AddCommandLine(args.Skip(1).Where(a => a.StartsWith("--")).ToArray()).Build();
var connectionString = Environment.GetEnvironmentVariable("BUILDER_DB")
    ?? config.GetConnectionString("Builder")
    ?? "Host=localhost;Port=15433;Database=builder;Username=builder;Password=builder";

var command = args.FirstOrDefault() ?? "status";
using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o => o.SingleLine = true).SetMinimumLevel(LogLevel.Information));

try
{
    using var migrator = new DatabaseMigrator(connectionString, new LoggerProviderAdapter(loggerFactory));
    switch (command)
    {
        case "up":
            await migrator.EnsureDatabaseAsync();
            migrator.Up(args.Length > 1 ? long.Parse(args[1]) : null);
            break;
        case "down":
            if (args.Length < 2) throw new ArgumentException("down needs a target version (0 = revert everything).");
            migrator.DownTo(long.Parse(args[1]));
            break;
        case "rollback":
            migrator.Rollback(args.Length > 1 ? int.Parse(args[1]) : 1);
            break;
        case "status":
            break;
        default:
            throw new ArgumentException($"Unknown command '{command}'. Use up, down, rollback or status.");
    }

    foreach (var (version, title, applied) in migrator.Status())
        Console.WriteLine($"{(applied ? "[x]" : "[ ]")} {version:000000} {title}");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

internal sealed class LoggerProviderAdapter(ILoggerFactory factory) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => factory.CreateLogger(categoryName);
    public void Dispose() { }
}
