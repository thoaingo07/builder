-- The agent asked to tear a deployment down; only it may fetch the environment's credentials for that.
ALTER TABLE deployments ADD COLUMN teardown_agent_id uuid;
