-- Projects group the work of an organization: repositories (and so runners, builds, deployments) belong to one
-- project. Connections, environments and secrets belong to a project, or to no project = shared by the whole
-- organization; a name a runner asks for resolves in its project first, then in the shared ones.
-- Existing data moves into a project named "Default" in each organization.

CREATE TABLE projects (
    id          uuid         PRIMARY KEY,
    org_id      uuid         NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name        varchar(100) NOT NULL,
    slug        varchar(60)  NOT NULL,
    description text,
    created_at  timestamptz  NOT NULL
);
CREATE UNIQUE INDEX ux_projects_org_slug ON projects (org_id, slug);
CREATE UNIQUE INDEX ux_projects_org_name ON projects (org_id, lower(name));

INSERT INTO projects (id, org_id, name, slug, created_at)
SELECT gen_random_uuid(), o.id, 'Default', 'default', now()
FROM organizations o
WHERE EXISTS (SELECT 1 FROM repositories r WHERE r.org_id = o.id);

-- repositories and runners: always in a project
ALTER TABLE repositories ADD COLUMN project_id uuid REFERENCES projects (id);
UPDATE repositories r SET project_id = p.id FROM projects p WHERE p.org_id = r.org_id AND p.slug = 'default';
ALTER TABLE repositories ALTER COLUMN project_id SET NOT NULL;
CREATE INDEX ix_repositories_project_id ON repositories (project_id);

ALTER TABLE pipelines ADD COLUMN project_id uuid REFERENCES projects (id);
UPDATE pipelines x SET project_id = r.project_id FROM repositories r WHERE r.id = x.repository_id;
ALTER TABLE pipelines ALTER COLUMN project_id SET NOT NULL;
DROP INDEX ux_pipelines_org_name;
CREATE UNIQUE INDEX ux_pipelines_project_name ON pipelines (project_id, name);

-- builds and deployments remember their project (lists and live updates filter on it)
ALTER TABLE builds ADD COLUMN project_id uuid REFERENCES projects (id);
UPDATE builds b SET project_id = p.project_id FROM pipelines p WHERE p.id = b.pipeline_id;
ALTER TABLE builds ALTER COLUMN project_id SET NOT NULL;
CREATE INDEX ix_builds_project_id ON builds (project_id);
ALTER TABLE deployments ADD COLUMN project_id uuid REFERENCES projects (id);
UPDATE deployments d SET project_id = b.project_id FROM builds b WHERE b.id = d.build_id;
ALTER TABLE deployments ALTER COLUMN project_id SET NOT NULL;
CREATE INDEX ix_deployments_project_id ON deployments (project_id);

-- connections, environments, secrets: a project's own, or shared (NULL); names are unique within each
ALTER TABLE connections  ADD COLUMN project_id uuid REFERENCES projects (id);
ALTER TABLE environments ADD COLUMN project_id uuid REFERENCES projects (id);
ALTER TABLE secrets      ADD COLUMN project_id uuid REFERENCES projects (id);

DROP INDEX ux_environments_org_name;
CREATE UNIQUE INDEX ux_environments_scope_name ON environments (org_id, COALESCE(project_id, '00000000-0000-0000-0000-000000000000'), name);
DROP INDEX ux_secrets_org_name;
CREATE UNIQUE INDEX ux_secrets_scope_name ON secrets (org_id, COALESCE(project_id, '00000000-0000-0000-0000-000000000000'), name);
CREATE INDEX ix_connections_project_id ON connections (project_id);
