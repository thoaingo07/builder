-- Google sign-in: users can be matched by e-mail, and a user may have no password (Google only).
ALTER TABLE users ADD COLUMN email text;
CREATE UNIQUE INDEX ux_users_email ON users (lower(email)) WHERE email IS NOT NULL;
ALTER TABLE users ALTER COLUMN password_hash DROP NOT NULL;
