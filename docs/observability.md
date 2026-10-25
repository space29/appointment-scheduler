# Logging, metrics, and tracing

The API configures the OpenTelemetry SDK at its composition boundary. Application and Infrastructure emit BCL `ActivitySource`/`Meter` instrumentation; Domain has no telemetry dependency. The service is named `appointment-scheduler`, with a unique instance ID for each process and a shared resource identity across logs, metrics, and traces.

## Inspect locally without Docker

Run the README migration command first: migration `003_outbox_trace_context.sql` adds nullable trace metadata to existing outbox events. Start the API normally for JSON console logs and `GET /metrics`. Both work without an external collector. The local-only metrics endpoint does not require a demo actor header.

```powershell
# After configuring the database and Development environment as in the README:
$env:Observability__ConsoleTracing = 'true'
dotnet run --project src/Scheduler.Api -c Release --no-build --no-launch-profile --urls http://127.0.0.1:5080
# In another terminal:
./scripts/demo.ps1 -BaseUrl http://127.0.0.1:5080
(Invoke-WebRequest http://127.0.0.1:5080/metrics).Content
```

`ConsoleTracing` prints human-readable span records alongside the JSON logs; leave it disabled for machine-readable JSON-only output. HTTP responses include `X-Trace-Id`, and Problem Details include the same distributed trace ID. Search the console log's `TraceId` scope field to follow a request. Booking and notification log records also include their span IDs. Notification failures are Warning-level records; unexpected HTTP failures are Error-level records with a classified error type.

## Optional local dashboard

```powershell
# Start the entire demo, using a disposable password as described in README:
docker compose -f compose.yaml -f compose.observability.yaml up --build
```

Open Aspire at `http://localhost:18888` for structured logs, trace waterfalls, and metrics; select `appointment-scheduler`. Prometheus at `http://localhost:9090` provides metric history, PromQL queries, and the Alerts page. The dashboard, OTLP receiver, and Prometheus host ports bind only to loopback. Anonymous dashboard access is an explicit local demonstration setting. The API keeps serving if the dashboard is unavailable; it has no collector readiness dependency.

To use the workstation's existing PostgreSQL/API instead of the Compose database, start only the dashboard, then configure the local API before restarting it:

```powershell
docker compose -f compose.yaml -f compose.observability.yaml up -d --no-deps dashboard
$env:Observability__OtlpEndpoint = 'http://127.0.0.1:4318'
# Restart the local API using the previous dotnet run command.
```

Compose still interpolates the base file, so set the disposable `POSTGRES_PASSWORD` variable even for dashboard-only startup. `Observability:OtlpEndpoint` is a base URL; the SDK sends protobuf over HTTP to `/v1/traces`, `/v1/metrics`, and `/v1/logs`. A blank endpoint disables network export. This application uses that setting explicitly rather than automatically exporting to an ambient OTLP endpoint. Metrics export periodically (SDK default: 60 seconds); traces/logs use bounded background batches. Export timeout is three seconds, independent of the booking transaction. These queues are best effort, not a durable telemetry outbox.

Docker is unavailable on the implementation workstation. The Compose viewer and Prometheus configuration are provided but have not been run there. Automated tests **do** send all three signal types to a real local HTTP receiver and verify the payloads, correlation, and behavior when it returns 503. The [standalone Aspire documentation](https://aspire.dev/dashboard/standalone/) describes its local viewing model and ports; the pinned image tag is listed in [Microsoft's image registry](https://mcr.microsoft.com/v2/dotnet/aspire-dashboard/tags/list).

## What is instrumented

| Signal | Implementation and interpretation |
| --- | --- |
| Logs | JSON console and optional OTLP export. Route template, method, status, elapsed time, distributed trace/span IDs; booking commit/attempt; notification attempt/outcome. No application logging of request bodies, contacts, VINs, SQL parameters, passwords, or provider content. |
| Request traces | ASP.NET Core server spans. Health and metrics requests are excluded from trace export. Query strings, raw URL paths, and user agents are removed from exported server spans. |
| Business traces | `booking.confirm`, `booking.cancel`, `appointment.start`, `appointment.progress`, `appointment.complete`, and `notification.deliver`. Booking rejection codes are span attributes; unexpected booking failures and failed delivery attempts are marked Error. |
| Database traces | Child client spans for connection acquisition, begin, execute, scalar/query, and commit. No SQL text, parameters, or connection string attributes. Connection duration includes pool acquisition; query duration can include lock waits but does not isolate them. |
| Async correlation | The initiating W3C `traceparent`/`tracestate` are saved with the outbox event. Each delivery attempt is a Consumer span parented to the saved span, including on a different worker. Legacy events with no context remain deliverable. Baggage is not persisted. |
| Sampling | Parent-based trace sampling; default root ratio 1 for the demo. Set `Observability__TraceSampleRatio` between 0 and 1 before startup. Unsampled parent decisions survive outbox delivery; counters and histograms still record the work. |

| Application metric | Unit / labels |
| --- | --- |
| `scheduler.booking.outcomes` | Counter, `outcome`: confirmed, replayed, error, or a bounded application rejection code |
| `scheduler.booking.duration` | Milliseconds histogram, same outcome label; includes 2,000/3,000 ms SLO boundaries |
| `scheduler.booking.contention` | Counter of caught PostgreSQL contention failures, `code` |
| `scheduler.notification.outcomes` | Counter of delivery handling outcomes, including Pending, Failed, ProviderAccepted, Suppressed |
| `scheduler.database.duration` | Milliseconds histogram, bounded operation/outcome labels |
| `scheduler.outbox.pending` / `scheduler.outbox.oldest.age` | Unmaterialized event count / oldest age in seconds |
| `scheduler.notification.pending` / `scheduler.notification.failed` | Pending-or-processing / exhausted delivery counts |
| `scheduler.execution.stale` / `scheduler.incident.open` | Unfinished expired forecasts / unresolved incidents |
| `scheduler.operations.snapshot.age` | Seconds since the last successful operational snapshot; detects stale evidence when database access fails |

ASP.NET Core request duration/status/rate and .NET runtime metrics are also collected. Prometheus converts dots to underscores and adds counter/unit suffixes; for example, `scheduler_booking_outcomes_total` and `scheduler_booking_duration_milliseconds_bucket`. Scope labels are supplied by the exporter. Business identifiers are not metric labels.

Operational gauges refresh every five seconds with one SQL snapshot query per API replica. Scraping reads cached state. Before the first successful snapshot, gauges emit no sample; after a failed refresh, previous values remain and snapshot age increases. Count gauges describe that snapshot, not a live query at scrape time. Oldest-event age advances from the cached oldest event timestamp. Replicas observe the **same database state**: use `max`, not `sum`, when aggregating those gauges. Per-process booking/HTTP counters can be summed. At larger deployment sizes, move database-wide polling to a dedicated observer and tune aggregate queries/indexes using measurements.

## Pair-review queries and alerts

Run the demo, copy a booking response's trace ID, and inspect its server span, `booking.confirm`, database children, and later delivery span. Toggle `Notifications__SimulateFailure=true` before restarting to observe Warning logs, Pending retries, Error delivery spans, and eventually the failed-delivery gauge. Restart with the flag disabled and use the existing staff retry endpoint to recover. This flag controls the mock provider only.

Useful Prometheus expressions:

```promql
# New commits per second; excludes same-quote replay.
sum(rate(scheduler_booking_outcomes_total{outcome="confirmed"}[5m]))

# Read request throughput; exclude operational endpoints.
sum(rate(http_server_request_duration_seconds_count{http_request_method="GET",http_route!~"/metrics|/health/.*|/openapi/.*"}[5m]))

# Successful confirmation p95 in milliseconds across API replicas.
histogram_quantile(0.95, sum by (le) (rate(scheduler_booking_duration_milliseconds_bucket{outcome="confirmed"}[5m])))

# Database-wide pending work; do not sum replicated observations.
max(scheduler_outbox_pending)
max(scheduler_notification_failed)
max(scheduler_execution_stale)
```

`observability/alerts.yml` supplies local rules for p95 >2s, p99 >3s, an old outbox backlog, exhausted deliveries, stale execution forecasts, missing/stale operational snapshots, and scrape unavailability. These appear in Prometheus; no Alertmanager, paging, or external notifications are configured. The non-latency thresholds are initial demo choices requiring operational tuning. With Docker available, validate syntax using:

```powershell
docker compose -f compose.yaml -f compose.observability.yaml run --rm --no-deps --entrypoint promtool prometheus check rules /etc/prometheus/alerts.yml
```

## Limits and production follow-up

The pinned OpenTelemetry Prometheus ASP.NET exporter is [prerelease](https://www.nuget.org/packages/OpenTelemetry.Exporter.Prometheus.AspNetCore/1.15.3-beta.1). OTLP is the stable export path; a production collector can route metrics to a production monitoring backend without relying on the local scrape exporter. The SDK setup follows the [official hosting guidance](https://www.nuget.org/packages/OpenTelemetry.Extensions.Hosting/1.15.3).

Production still needs authenticated telemetry access, managed retention/storage, collector availability, alert routing, independent readiness/synthetic booking probes, database-native pool/lock/CPU/I/O/replication monitoring, and measured sampling/log-volume costs. Aspire's local viewer is not the production telemetry store. No claim of 99.9% uptime follows from a healthy scrape or a short load test; monthly availability needs a defined SLI and observations over time. The latest short benchmark enables instrumentation but leaves OTLP export off and logs at Warning, so it does not establish capacity with a full telemetry backend attached.
