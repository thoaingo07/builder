-- Steps: the cmds of a job's task, planned from the runner file and updated live from the agent's step markers.
ALTER TABLE build_jobs ADD COLUMN steps jsonb NOT NULL DEFAULT '[]';
-- which step a log line belongs to (null: setup lines before the first step)
ALTER TABLE log_lines ADD COLUMN step integer;
