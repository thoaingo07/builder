-- SSH + Docker environments can log in with a password (ciphertext) instead of a private key.
ALTER TABLE environments ADD COLUMN password_protected text;
