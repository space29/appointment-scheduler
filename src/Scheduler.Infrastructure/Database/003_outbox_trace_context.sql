-- Nullable for existing events. Trace context is metadata, not part of the business event payload.
ALTER TABLE outbox_message ADD COLUMN trace_parent varchar(55), ADD COLUMN trace_state varchar(512);
