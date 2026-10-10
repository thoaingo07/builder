-- Triggers: builds start on push, pull request or schedule (x-builder.triggers in the runner file).

-- why a build ran, and what it built for pull requests
ALTER TABLE builds ADD COLUMN reason varchar(32) NOT NULL DEFAULT 'Manual';   -- Manual | Push | PullRequest | Schedule | Rerun
ALTER TABLE builds ADD COLUMN source_ref text;                                 -- e.g. refs/pull/12/merge
ALTER TABLE builds ADD COLUMN pull_request_id integer;
CREATE INDEX ix_builds_pipeline_commit ON builds (pipeline_id, commit);

-- webhook secret per repository (SHA-256; the secret is shown once)
ALTER TABLE repositories ADD COLUMN hook_secret_hash text;

-- triggers of each runner as found on its repository's default branch (schedules need them between pushes)
ALTER TABLE pipelines ADD COLUMN triggers jsonb;
ALTER TABLE pipelines ADD COLUMN triggers_commit text;
ALTER TABLE pipelines ADD COLUMN triggers_error text;

CREATE TABLE runner_schedules (
    id          uuid        PRIMARY KEY,
    org_id      uuid        NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    pipeline_id uuid        NOT NULL REFERENCES pipelines (id) ON DELETE CASCADE,
    cron        text        NOT NULL,
    time_zone   text        NOT NULL DEFAULT 'UTC',
    branch      text        NOT NULL,
    vars        jsonb       NOT NULL DEFAULT '{}',
    next_run_at timestamptz,
    last_run_at timestamptz
);
CREATE INDEX ix_runner_schedules_next ON runner_schedules (next_run_at);
CREATE INDEX ix_runner_schedules_pipeline ON runner_schedules (pipeline_id);
