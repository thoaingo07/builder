// Local orchestration for development and tests.
//
//   dotnet run --project src/Builder.AppHost            → everything + Aspire dashboard
//   Builder:Profile = db       → PostgreSQL only (database tests)
//   Builder:Profile = backend  → PostgreSQL, migrator, API, BFF, two agents (end-to-end tests)
//   Builder:Ephemeral = true   → no data volume, data under a temp directory (tests)
//   Builder:TestVpsAuthorizedKey = <ssh public key> → adds the "vps" container for deploy tests
//
// The secrets below are for local use only; real deployments use deploy/compose.yml and its .env.

// Containers run on Podman when it is installed (this project's default), unless ASPIRE_CONTAINER_RUNTIME says otherwise.
if (Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") is null && OnPath("podman"))
    Environment.SetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME", "podman");

var builder = DistributedApplication.CreateBuilder(args);

var profile = builder.Configuration["Builder:Profile"] ?? "full";
var ephemeral = bool.TryParse(builder.Configuration["Builder:Ephemeral"], out var e) && e;
var dataRoot = builder.Configuration["Builder:DataDirectory"]
    ?? (ephemeral
        ? Path.Combine(Path.GetTempPath(), "builder-aspire-" + Guid.NewGuid().ToString("N")[..8])
        : Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "../../data/aspire")));

const string agentToken = "aspire-agent-token";
// dev keeps the familiar admin/admin; the ephemeral test stack uses a password that passes the 12-character rule
var adminPassword = ephemeral ? "test-admin-password" : "admin";
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
    .WithEnvironment("Auth__AdminPassword", adminPassword)
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

// Deploy tests: a stand-in VPS (sshd + compose CLI) that drives this machine's Podman/Docker through its socket.
if (builder.Configuration["Builder:TestVpsAuthorizedKey"] is { Length: > 0 } authorizedKey)
{
    builder.AddDockerfile("vps", "../../deploy/test-vps", "Containerfile")
        .WithEnvironment("AUTHORIZED_KEY", authorizedKey)
        .WithBindMount(ContainerSocket(), "/var/run/docker.sock")
        .WithEndpoint(targetPort: 22, name: "ssh", scheme: "tcp");
}

if (profile == "full")
{
    builder.AddViteApp("ui", "../../ui")
        .WithEnvironment("BUILDER_BFF_URL", bff.GetEndpoint("http"))
        .WaitFor(bff)
        .WithExternalHttpEndpoints();
}

builder.Build().Run();

static string ContainerSocket() =>
    Environment.GetEnvironmentVariable("ASPIRE_CONTAINER_RUNTIME") == "podman"
        ? Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR")
            ?? throw new InvalidOperationException("XDG_RUNTIME_DIR is not set; cannot find the rootless Podman socket."), "podman/podman.sock")
        : "/var/run/docker.sock";

static bool OnPath(string tool) =>
    (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
    .Any(dir => File.Exists(Path.Combine(dir, tool)));
