-- Back to a single tenant: everything is kept, organizations/memberships/repositories are dropped.
-- Pipeline/environment names that are only unique per organization must be unique globally again;
-- duplicates get the organization slug appended.

ALTER TABLE pipelines ADD COLUMN repository_url text;
ALTER TABLE pipelines ADD COLUMN connection_id  uuid REFERENCES connections (id);
ALTER TABLE pipelines ADD COLUMN default_branch text;
UPDATE pipelines p SET repository_url = r.url, connection_id = r.connection_id, default_branch = r.default_branch
FROM repositories r WHERE r.id = p.repository_id;
ALTER TABLE pipelines ALTER COLUMN repository_url SET NOT NULL;
ALTER TABLE pipelines ALTER COLUMN default_branch SET NOT NULL;

DROP INDEX IF EXISTS ux_pipelines_org_name;
DROP INDEX IF EXISTS ux_pipelines_repository_file;
UPDATE pipelines p SET name = p.name || '-' || o.slug
FROM organizations o
WHERE o.id = p.org_id AND (SELECT count(*) FROM pipelines x WHERE x.name = p.name) > 1;
CREATE UNIQUE INDEX ux_pipelines_name ON pipelines (name);

DROP INDEX IF EXISTS ux_environments_org_name;
UPDATE environments e SET name = e.name || '-' || o.slug
FROM organizations o
WHERE o.id = e.org_id AND (SELECT count(*) FROM environments x WHERE x.name = e.name) > 1;
CREATE UNIQUE INDEX ux_environments_name ON environments (name);

DROP INDEX IF EXISTS ux_agents_org_name;
UPDATE agents a SET name = a.name || '-' || o.slug
FROM organizations o
WHERE o.id = a.org_id AND (SELECT count(*) FROM agents x WHERE x.name = a.name) > 1;
CREATE UNIQUE INDEX ux_agents_name ON agents (name);

ALTER TABLE pipelines    DROP COLUMN repository_id;
ALTER TABLE pipelines    DROP COLUMN org_id;
ALTER TABLE connections  DROP COLUMN org_id;
ALTER TABLE environments DROP COLUMN org_id;
ALTER TABLE builds       DROP COLUMN org_id;
ALTER TABLE deployments  DROP COLUMN org_id;
ALTER TABLE agents       DROP COLUMN org_id;

DROP TABLE repositories;
DROP TABLE memberships;
DROP TABLE organizations;
