ALTER TABLE build_jobs DROP COLUMN azure_artifacts;
ALTER TABLE build_jobs DROP COLUMN registries;
-- service-principal and Azure connections cannot exist without these columns: detach their repositories, then drop them
UPDATE repositories SET connection_id = NULL
WHERE connection_id IN (SELECT id FROM connections WHERE auth_kind <> 'Pat' OR type = 'Azure');
DELETE FROM connections WHERE auth_kind <> 'Pat' OR type = 'Azure';
ALTER TABLE connections DROP COLUMN client_id;
ALTER TABLE connections DROP COLUMN tenant_id;
ALTER TABLE connections DROP COLUMN auth_kind;
