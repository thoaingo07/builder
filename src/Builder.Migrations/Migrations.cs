using FluentMigrator;

namespace Builder.Migrations;

// Every migration in db/migrations, one line each: the version and title of its file pair.
// The SQL lives in the files; a class only names them. `task migrate:create -- <title>` writes a new
// pair and appends its line here. MigrationCatalog refuses to run if a file and a line disagree.
#pragma warning disable SA1649, SA1402 // many tiny types in one file, on purpose

[Migration(1, "initial_schema")] public sealed class M000001 : SqlFileMigration;
[Migration(2, "user_email")] public sealed class M000002 : SqlFileMigration;
[Migration(3, "organizations")] public sealed class M000003 : SqlFileMigration;
[Migration(4, "secrets")] public sealed class M000004 : SqlFileMigration;
[Migration(5, "short_lived_credentials")] public sealed class M000005 : SqlFileMigration;
[Migration(6, "teardown_agent")] public sealed class M000006 : SqlFileMigration;
[Migration(7, "job_steps")] public sealed class M000007 : SqlFileMigration;
[Migration(8, "triggers")] public sealed class M000008 : SqlFileMigration;
[Migration(9, "container_deployments")] public sealed class M000009 : SqlFileMigration;
[Migration(10, "projects")] public sealed class M000010 : SqlFileMigration;
