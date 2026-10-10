-- Container (blue-green / recreate) deployments keep their settings for rollback and destroy.
ALTER TABLE deployments ADD COLUMN container jsonb;
