-- Google-only users cannot exist without the email column; remove them before restoring NOT NULL.
DELETE FROM users WHERE password_hash IS NULL;
ALTER TABLE users ALTER COLUMN password_hash SET NOT NULL;
DROP INDEX IF EXISTS ux_users_email;
ALTER TABLE users DROP COLUMN email;
