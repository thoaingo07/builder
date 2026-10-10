-- Fails if two projects of an organization have a runner, environment or secret with the same name.
DROP INDEX ix_connections_project_id;
DROP INDEX ux_secrets_scope_name;
CREATE UNIQUE INDEX ux_secrets_org_name ON secrets (org_id, name);
DROP INDEX ux_environments_scope_name;
CREATE UNIQUE INDEX ux_environments_org_name ON environments (org_id, name);
ALTER TABLE secrets      DROP COLUMN project_id;
ALTER TABLE environments DROP COLUMN project_id;
ALTER TABLE connections  DROP COLUMN project_id;

ALTER TABLE deployments DROP COLUMN project_id;
ALTER TABLE builds DROP COLUMN project_id;
DROP INDEX ux_pipelines_project_name;
CREATE UNIQUE INDEX ux_pipelines_org_name ON pipelines (org_id, name);
ALTER TABLE pipelines DROP COLUMN project_id;
ALTER TABLE repositories DROP COLUMN project_id;
DROP TABLE projects;
