// Local orchestration for development and tests.
//
//   dotnet run --project src/Builder.AppHost            → everything + Aspire dashboard
//   Builder:Profile = db       → PostgreSQL only (database tests)
//   Builder:Profile = backend  → PostgreSQL, migrator, API, BFF, two agents (end-to-end tests)
//   Builder:Ephemeral = true   → no data volume, data under a temp directory (tests)
//
// The secrets below are for local use only; real deployments use deploy/compose.yml and its .env.

var builder = DistributedApplication.CreateBuilder(args);

var profile = builder.Configuration["Builder:Profile"] ?? "full";
var ephemeral = bool.TryParse(builder.Configuration["Builder:Ephemeral"], out var e) && e;
var dataRoot = builder.Configuration["Builder:DataDirectory"]
    ?? (ephemeral
        ? Path.Combine(Path.GetTempPath(), "builder-aspire-" + Guid.NewGuid().ToString("N")[..8])
        : Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../data/aspire")));

const string agentToken = "aspire-agent-token";
const string jwtKey = "aspire-only-signing-key-0123456789abcdef0123";

var postgres = builder.AddPostgres("postgres").WithImageTag("17-alpine");
if (!ephemeral)
    postgres.WithDataVolume("builder-aspire-pg").WithLifetime(ContainerLifetime.Persistent);
var db = postgres.AddDatabase("builder");

if (profile == "db")
{
    builder.Build().Run();
    return;
}

var migrate = builder.AddProject<Projects.Builder_Migrations>("migrate")
    .WithArgs("up")
    .WithReference(db)
    .WaitFor(db);

var api = builder.AddProject<Projects.Builder_Api>("api")
    .WithReference(db)
    .WaitForCompletion(migrate)
    .WithEnvironment("Database__MigrateOnStartup", "false")
    .WithEnvironment("Storage__DataDirectory", Path.Combine(dataRoot, "api"))
    .WithEnvironment("Auth__AdminUser", "admin")
    .WithEnvironment("Auth__AdminPassword", "admin")
    .WithEnvironment("Auth__Jwt__SigningKey", jwtKey)
    .WithEnvironment("Agents__Token", agentToken);

var bff = builder.AddProject<Projects.Builder_Bff>("bff")
    .WithReference(api)
    .WaitFor(api)
    .WithEnvironment("Api__Url", api.GetEndpoint("http"))
    .WithEnvironment("DataDirectory", Path.Combine(dataRoot, "bff"))
    .WithEnvironment("Auth__Jwt__SigningKey", jwtKey)
    .WithExternalHttpEndpoints();

foreach (var name in new[] { "agent-1", "agent-2" })
{
    builder.AddProject<Projects.Builder_Agent>(name)
        .WaitFor(api)
        .WithEnvironment("Agent__ServerUrl", api.GetEndpoint("http"))
        .WithEnvironment("Agent__Token", agentToken)
        .WithEnvironment("Agent__Name", name)
        .WithEnvironment("Agent__WorkDirectory", Path.Combine(dataRoot, name));
}

if (profile == "full")
{
    builder.AddViteApp("ui", "../../ui")
        .WithEnvironment("BUILDER_BFF_URL", bff.GetEndpoint("http"))
        .WaitFor(bff)
        .WithExternalHttpEndpoints();
}

builder.Build().Run();
