-- Organization secrets. Values are Data Protection ciphertext and never leave the API except to the agent running
-- a job that declares them (x-secrets), at run time.
CREATE TABLE secrets (
    id              uuid         PRIMARY KEY,
    org_id          uuid         NOT NULL REFERENCES organizations (id) ON DELETE CASCADE,
    name            varchar(100) NOT NULL,
    value_protected text         NOT NULL,
    description     text,
    created_at      timestamptz  NOT NULL,
    updated_at      timestamptz  NOT NULL,
    updated_by      text         NOT NULL
);
CREATE UNIQUE INDEX ux_secrets_org_name ON secrets (org_id, name);

-- the secret names a job declared (x-secrets)
ALTER TABLE build_jobs ADD COLUMN secrets text[] NOT NULL DEFAULT '{}';
