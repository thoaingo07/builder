# Database migrations

The schema is owned by raw SQL files, applied by FluentMigrator (`src/Builder.Migrations`).
EF Core only reads and writes rows; it never creates or alters tables.

- Every migration is a pair: `NNNNNN_title.up.sql` and `NNNNNN_title.down.sql` (6-digit version, `snake_case` title),
  plus **one line** in `src/Builder.Migrations/Migrations.cs`:
  `[Migration(2, "add_something")] public sealed class M000002 : SqlFileMigration;`
- The migrator refuses to run if a file and a line disagree. Each file runs in one transaction.
- Applied versions are recorded in `public."VersionInfo"`.
- Use snake_case identifiers; EF maps them via `UseSnakeCaseNamingConvention()`. When you add a column,
  add the property to the entity too.

```bash
task migrate:up               # apply everything pending
task migrate:down -- 0        # revert to version 0 (runs the .down.sql files, newest first)
task migrate:rollback         # revert the newest migration
task migrate:status
task migrate:create -- add_build_tags   # writes the next pair + its line in Migrations.cs
```

Connection string: `BUILDER_DB` (or `ConnectionStrings__Builder`), default = the dev database on `localhost:15433`.
