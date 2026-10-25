CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE TABLE customer (id uuid PRIMARY KEY, name text NOT NULL);
CREATE TABLE vehicle (id uuid PRIMARY KEY, customer_id uuid NOT NULL REFERENCES customer, category text NOT NULL);
CREATE TABLE dealership (id uuid PRIMARY KEY, name text NOT NULL, time_zone text NOT NULL);
CREATE TABLE service_type (id uuid PRIMARY KEY, name text NOT NULL, baseline_minutes int NOT NULL CHECK (baseline_minutes > 0));
CREATE TABLE duration_rule (
 id uuid PRIMARY KEY, service_type_id uuid NOT NULL REFERENCES service_type, category text NOT NULL,
 estimated_minutes int NOT NULL CHECK (estimated_minutes > 0), buffer_minutes int NOT NULL CHECK (buffer_minutes >= 0),
 version int NOT NULL, UNIQUE(service_type_id, category)
);
CREATE TABLE price_policy (
 id uuid PRIMARY KEY, dealership_id uuid NOT NULL REFERENCES dealership, service_type_id uuid NOT NULL REFERENCES service_type,
 base_price numeric(12,2) NOT NULL CHECK(base_price >= 0), currency text NOT NULL CHECK(length(currency)=3), version int NOT NULL,
 UNIQUE(dealership_id, service_type_id)
);
-- Non-overlapping bands within a local weekday. Overnight bands must be split at midnight.
CREATE TABLE price_band (
 id uuid PRIMARY KEY, policy_id uuid NOT NULL REFERENCES price_policy, name text NOT NULL,
 weekday int NOT NULL CHECK(weekday BETWEEN 0 AND 6), start_minute int NOT NULL CHECK(start_minute BETWEEN 0 AND 1439),
 end_minute int NOT NULL CHECK(end_minute BETWEEN 1 AND 1440), multiplier numeric(6,3) NOT NULL CHECK(multiplier > 0),
 CHECK(end_minute > start_minute),
 EXCLUDE USING gist(policy_id WITH =, weekday WITH =, int4range(start_minute,end_minute,'[)') WITH &&)
);
CREATE TABLE resource (id uuid PRIMARY KEY, dealership_id uuid NOT NULL REFERENCES dealership, kind text NOT NULL CHECK(kind IN ('Bay','Technician')), name text NOT NULL);
CREATE TABLE qualification (resource_id uuid NOT NULL REFERENCES resource, service_type_id uuid NOT NULL REFERENCES service_type, PRIMARY KEY(resource_id, service_type_id));
CREATE TABLE availability (id uuid PRIMARY KEY, resource_id uuid NOT NULL REFERENCES resource, starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL, CHECK(ends_at>starts_at));
CREATE INDEX availability_resource ON availability(resource_id, starts_at, ends_at);
CREATE TABLE appointment_quote (
 id uuid PRIMARY KEY, customer_id uuid NOT NULL REFERENCES customer, vehicle_id uuid NOT NULL REFERENCES vehicle,
 dealership_id uuid NOT NULL REFERENCES dealership, service_type_id uuid NOT NULL REFERENCES service_type,
 starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL, estimated_minutes int NOT NULL CHECK(estimated_minutes>0),
 buffer_minutes int NOT NULL CHECK(buffer_minutes>=0), duration_version int NOT NULL,
 base_price numeric(12,2) NOT NULL, multiplier numeric(6,3) NOT NULL, band text NOT NULL,
 total numeric(12,2) NOT NULL CHECK(total>=0), currency text NOT NULL, price_version int NOT NULL,
 created_at timestamptz NOT NULL, expires_at timestamptz NOT NULL, CHECK(ends_at>starts_at)
);
CREATE TABLE appointment (
 id uuid PRIMARY KEY, quote_id uuid NOT NULL UNIQUE REFERENCES appointment_quote,
 customer_id uuid NOT NULL REFERENCES customer, vehicle_id uuid NOT NULL REFERENCES vehicle,
 dealership_id uuid NOT NULL REFERENCES dealership, service_type_id uuid NOT NULL REFERENCES service_type,
 starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL, original_starts_at timestamptz NOT NULL, original_ends_at timestamptz NOT NULL,
 estimated_minutes int NOT NULL CHECK(estimated_minutes>0), buffer_minutes int NOT NULL CHECK(buffer_minutes>=0),
 duration_version int NOT NULL, price_version int NOT NULL, total numeric(12,2) NOT NULL CHECK(total>=0), currency text NOT NULL,
 payment_timing text NOT NULL CHECK(payment_timing='PayAtCheckIn'), booking_channel text NOT NULL CHECK(booking_channel='Web'),
 status text NOT NULL CHECK(status IN ('Confirmed','InProgress','Completed','Cancelled')), revision int NOT NULL DEFAULT 1,
 created_at timestamptz NOT NULL, cancelled_at timestamptz, cancellation_reason text, cancelled_by uuid,
 CHECK(ends_at>starts_at)
);
CREATE INDEX appointment_customer ON appointment(customer_id, created_at DESC);
CREATE TABLE resource_reservation (
 id uuid PRIMARY KEY, appointment_id uuid NOT NULL REFERENCES appointment, resource_id uuid NOT NULL REFERENCES resource,
 role text NOT NULL CHECK(role IN ('Bay','LeadTechnician','SupportTechnician')),
 starts_at timestamptz NOT NULL, ends_at timestamptz NOT NULL, is_active boolean NOT NULL DEFAULT true,
 released_at timestamptz, release_reason text, CHECK(isfinite(starts_at) AND isfinite(ends_at) AND ends_at>starts_at),
 CONSTRAINT reservation_no_overlap EXCLUDE USING gist(resource_id WITH =, tstzrange(starts_at,ends_at,'[)') WITH &&) WHERE (is_active)
);
CREATE INDEX reservation_appointment ON resource_reservation(appointment_id);
CREATE TABLE job_execution (
 id uuid PRIMARY KEY, appointment_id uuid NOT NULL UNIQUE REFERENCES appointment,
 actual_start timestamptz NOT NULL, expected_completion timestamptz NOT NULL, actual_completion timestamptz,
 revision int NOT NULL DEFAULT 1, CHECK(actual_completion IS NULL OR actual_completion>=actual_start)
);
CREATE TABLE resource_usage (
 id uuid PRIMARY KEY, execution_id uuid NOT NULL REFERENCES job_execution, resource_id uuid NOT NULL REFERENCES resource,
 acquired_at timestamptz NOT NULL, released_at timestamptz, UNIQUE(execution_id, resource_id)
);
CREATE UNIQUE INDEX one_active_job_per_resource ON resource_usage(resource_id) WHERE released_at IS NULL;
CREATE TABLE disruption_incident (
 id uuid PRIMARY KEY, execution_id uuid NOT NULL REFERENCES job_execution, affected_appointment_id uuid NOT NULL REFERENCES appointment,
 reason text NOT NULL, status text NOT NULL CHECK(status IN ('Open','Resolved')), updated_at timestamptz NOT NULL,
 UNIQUE(execution_id,affected_appointment_id)
);
CREATE TABLE execution_report (
 id uuid PRIMARY KEY, execution_id uuid NOT NULL REFERENCES job_execution, actor_id uuid NOT NULL,
 expected_completion timestamptz NOT NULL, reason text NOT NULL, created_at timestamptz NOT NULL
);
CREATE TABLE outbox_message (
 id uuid PRIMARY KEY, appointment_id uuid NOT NULL REFERENCES appointment, event_type text NOT NULL,
 revision int NOT NULL, recipients uuid[] NOT NULL, payload jsonb NOT NULL,
 created_at timestamptz NOT NULL, processed_at timestamptz, UNIQUE(appointment_id,event_type,revision)
);
CREATE INDEX outbox_pending ON outbox_message(created_at) WHERE processed_at IS NULL;
CREATE TABLE notification_delivery (
 id uuid PRIMARY KEY, event_id uuid NOT NULL REFERENCES outbox_message, recipient_id uuid NOT NULL,
 status text NOT NULL CHECK(status IN ('Pending','Processing','ProviderAccepted','Failed','Suppressed')),
 attempts int NOT NULL DEFAULT 0, next_attempt_at timestamptz NOT NULL, lease_token uuid, lease_until timestamptz,
 last_error text, read_at timestamptz, UNIQUE(event_id,recipient_id)
);
CREATE INDEX delivery_due ON notification_delivery(next_attempt_at) WHERE status IN ('Pending','Processing');
CREATE INDEX delivery_recipient ON notification_delivery(recipient_id, next_attempt_at DESC);
