-- Connections can authenticate with an Entra service principal (client credentials) instead of a PAT:
-- the API then mints short-lived tokens per job. New connection type 'Azure' (ARM / container registries).
ALTER TABLE connections ADD COLUMN auth_kind varchar(32) NOT NULL DEFAULT 'Pat';   -- Pat | ServicePrincipal
ALTER TABLE connections ADD COLUMN tenant_id text;
ALTER TABLE connections ADD COLUMN client_id text;                                   -- token_protected = client secret

-- what a job asked for: container registries to log in to (x-registries), Azure Artifacts feeds (x-azure-artifacts)
ALTER TABLE build_jobs ADD COLUMN registries jsonb NOT NULL DEFAULT '[]';
ALTER TABLE build_jobs ADD COLUMN azure_artifacts boolean NOT NULL DEFAULT false;
