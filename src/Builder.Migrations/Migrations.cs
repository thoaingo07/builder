using FluentMigrator;

namespace Builder.Migrations;

// Every migration in db/migrations, one line each: the version and title of its file pair.
// The SQL lives in the files; a class only names them. `task migrate:create -- <title>` writes a new
// pair and appends its line here. MigrationCatalog refuses to run if a file and a line disagree.
#pragma warning disable SA1649, SA1402 // many tiny types in one file, on purpose

[Migration(1, "initial_schema")] public sealed class M000001 : SqlFileMigration;
[Migration(2, "user_email")] public sealed class M000002 : SqlFileMigration;
