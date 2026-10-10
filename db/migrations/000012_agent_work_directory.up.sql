-- Per-agent work folder set in Builder (NULL: the agent's own default), what the agent reports as its default,
-- the folder it actually uses, and why it could not switch.
ALTER TABLE agents ADD COLUMN work_directory text;
ALTER TABLE agents ADD COLUMN default_work_directory text;
ALTER TABLE agents ADD COLUMN effective_work_directory text;
ALTER TABLE agents ADD COLUMN work_directory_error text;
