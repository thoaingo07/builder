-- Organizations. Users belong to organizations (memberships); everything a team configures or runs is scoped
-- to one organization. Agents are either an organization's own (org_id set) or shared by all (org_id NULL).
-- Pipelines become "runners": a mapping to one Taskfile under .builder/runners/ of a repository.
-- Existing data moves into an organization named "Default".

CREATE TABLE organizations (
    id               uuid         PRIMARY KEY,
    name             varchar(100) NOT NULL,
    slug             varchar(60)  NOT NULL,
    agent_token_hash text,                          -- SHA-256 of the org's agent registration token
    created_by       text         NOT NULL,
    created_at       timestamptz  NOT NULL
);
CREATE UNIQUE INDEX ux_organizations_slug ON organizations (slug);
CREATE UNIQUE INDEX ux_organizations_agent_token ON organizations (agent_token_hash) WHERE agent_token_hash IS NOT NULL;

CREATE TABLE memberships (
    org_id     uuid        NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    user_id    uuid        NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    role       varchar(32) NOT NULL,                -- Owner | Admin | Member
    created_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, user_id)
);
CREATE INDEX ix_memberships_user_id ON memberships (user_id);

CREATE TABLE repositories (
    id             uuid        PRIMARY KEY,
    org_id         uuid        NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    connection_id  uuid        REFERENCES connections (id),
    name           text        NOT NULL,
    url            text        NOT NULL,
    default_branch text        NOT NULL,
    created_at     timestamptz NOT NULL
);
CREATE UNIQUE INDEX ux_repositories_org_url ON repositories (org_id, url);

-- the Default organization for existing data, owned by every existing admin, joined by everyone else
INSERT INTO organizations (id, name, slug, created_by, created_at)
SELECT '00000000-0000-7000-8000-000000000001', 'Default', 'default', 'migration', now()
WHERE EXISTS (SELECT 1 FROM users) OR EXISTS (SELECT 1 FROM pipelines) OR EXISTS (SELECT 1 FROM agents);
INSERT INTO memberships (org_id, user_id, role, created_at)
SELECT o.id, u.id, CASE WHEN u.is_admin THEN 'Owner' ELSE 'Member' END, now()
FROM users u CROSS JOIN organizations o WHERE o.slug = 'default';

ALTER TABLE connections  ADD COLUMN org_id uuid REFERENCES organizations (id) ON DELETE CASCADE;
ALTER TABLE environments ADD COLUMN org_id uuid REFERENCES organizations (id) ON DELETE CASCADE;
ALTER TABLE pipelines    ADD COLUMN org_id uuid REFERENCES organizations (id) ON DELETE CASCADE;
ALTER TABLE builds       ADD COLUMN org_id uuid REFERENCES organizations (id) ON DELETE CASCADE;
ALTER TABLE deployments  ADD COLUMN org_id uuid;
ALTER TABLE agents       ADD COLUMN org_id uuid REFERENCES organizations (id) ON DELETE CASCADE;  -- NULL = shared
ALTER TABLE pipelines    ADD COLUMN repository_id uuid REFERENCES repositories (id) ON DELETE CASCADE;

UPDATE connections  SET org_id = '00000000-0000-7000-8000-000000000001';
UPDATE environments SET org_id = '00000000-0000-7000-8000-000000000001';
UPDATE pipelines    SET org_id = '00000000-0000-7000-8000-000000000001';
UPDATE builds       SET org_id = '00000000-0000-7000-8000-000000000001';
UPDATE deployments  SET org_id = '00000000-0000-7000-8000-000000000001';
-- existing agents registered with the system token: they stay shared (org_id NULL)

-- one repository per distinct pipeline repository URL
INSERT INTO repositories (id, org_id, connection_id, name, url, default_branch, created_at)
SELECT gen_random_uuid(), p.org_id, (array_agg(p.connection_id))[1],
       regexp_replace(regexp_replace(p.repository_url, '\.git$', ''), '^.*[/:]', ''),
       p.repository_url, (array_agg(p.default_branch))[1], min(p.created_at)
FROM pipelines p GROUP BY p.org_id, p.repository_url;
UPDATE pipelines p SET repository_id = r.id FROM repositories r WHERE r.org_id = p.org_id AND r.url = p.repository_url;

ALTER TABLE connections  ALTER COLUMN org_id SET NOT NULL;
ALTER TABLE environments ALTER COLUMN org_id SET NOT NULL;
ALTER TABLE pipelines    ALTER COLUMN org_id SET NOT NULL;
ALTER TABLE pipelines    ALTER COLUMN repository_id SET NOT NULL;
ALTER TABLE builds       ALTER COLUMN org_id SET NOT NULL;
ALTER TABLE deployments  ALTER COLUMN org_id SET NOT NULL;

-- the repository now carries url / connection / default branch
ALTER TABLE pipelines DROP COLUMN repository_url;
ALTER TABLE pipelines DROP COLUMN connection_id;
ALTER TABLE pipelines DROP COLUMN default_branch;

-- names are unique per organization now
DROP INDEX ux_pipelines_name;
CREATE UNIQUE INDEX ux_pipelines_org_name ON pipelines (org_id, name);
CREATE UNIQUE INDEX ux_pipelines_repository_file ON pipelines (repository_id, taskfile_path);
DROP INDEX ux_environments_name;
CREATE UNIQUE INDEX ux_environments_org_name ON environments (org_id, name);

CREATE INDEX ix_connections_org_id  ON connections (org_id);
CREATE INDEX ix_builds_org_id       ON builds (org_id);
CREATE INDEX ix_deployments_org_id  ON deployments (org_id);
CREATE INDEX ix_agents_org_id       ON agents (org_id);

-- agent names are unique within an organization (shared agents form their own namespace)
DROP INDEX ux_agents_name;
CREATE UNIQUE INDEX ux_agents_org_name ON agents (COALESCE(org_id, '00000000-0000-0000-0000-000000000000'), name);
