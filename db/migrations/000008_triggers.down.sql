DROP TABLE runner_schedules;
ALTER TABLE pipelines DROP COLUMN triggers_error;
ALTER TABLE pipelines DROP COLUMN triggers_commit;
ALTER TABLE pipelines DROP COLUMN triggers;
ALTER TABLE repositories DROP COLUMN hook_secret_hash;
DROP INDEX IF EXISTS ix_builds_pipeline_commit;
ALTER TABLE builds DROP COLUMN pull_request_id;
ALTER TABLE builds DROP COLUMN source_ref;
ALTER TABLE builds DROP COLUMN reason;
