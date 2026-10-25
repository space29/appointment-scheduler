# Recorded validation

Date: 25 October 2026 (Asia/Saigon). These observations apply to this implementation and local environment, not to an unprovisioned production system.

## Correctness and execution

| Check | Result |
| --- | --- |
| Release build, all six projects | Passed, zero warnings and errors after the observability implementation |
| Automated tests | 80 passed, zero failed/skipped: 24 domain/rule examples, four dependency/mutation-boundary checks, and 52 PostgreSQL/API/observability scenarios |
| Tests without a database | All 28 domain/rule/architecture examples passed independently |
| Migration compatibility | Original migration identifiers retained; migration 003 adds nullable trace context to existing outbox data, and repeated migrations preserve the booking |
| Concurrent API hosts | Verified final-slot exclusion, alternative allocation, and same-quote replay using independent host/connection pools |
| Atomic failure injection | A deliberately rejected outbox insert rolled back appointment and both reservations |
| Operations | Verified cancellation/start race, both booking/forecast commit orders, downstream impact without moving B, stale forecasts, and actual completion with insufficient remaining capacity for B |
| Notifications | Verified provider failure, retry, competing dispatchers, abandoned claim recovery, exhausted failure/manual retry, ownership, and obsolete confirmation suppression |
| Observability | Six BDD scenarios passed: actual OTLP/HTTP logs/metrics/traces, request-to-booking-to-database-to-worker correlation, retry errors/recovery, stale forecast/freshness gauges, legacy events, collector 503 isolation, and unsampled trace propagation with metrics preserved |
| Local viewers | Aspire/Prometheus Compose configuration and alert rules prepared; Docker execution unverified because Docker is unavailable |
| Local migration and seed | Passed against `appointment_scheduler`, including the additive outbox trace-context migration |
| Actual HTTP demo | Passed against Kestrel at localhost:5080: quote, booking, same-quote retry, cancellation, and mock notification inbox |
| Observability HTTP smoke | Passed against a temporary Kestrel process: quote/commit/replay/cancel, two background delivery acceptances, JSON TraceId/SpanId matching the response header, and a 200 metrics scrape containing booking and operational measurements |
| NuGet vulnerability check | No known vulnerable direct/transitive API packages reported by configured sources at check time |

The rollback test deliberately emits a classified error log for PostgreSQL state 23514. That is the expected failure injection, not a failed test. The test and load databases are isolated, uniquely named, and removed by their runners. The application demonstration database remains available locally.

BDD refactor verification originally passed 51 examples after splitting combined behaviors from the prior 31-case suite. At that stage application behavior and the recorded performance workload were unchanged. The subsequent Clean Architecture/DDD refactor added 23 domain, architecture, and migration checks. `dotnet test tests/Scheduler.Tests/Scheduler.Tests.csproj -c Release --no-restore` passed all 74 examples with none skipped on .NET runtime 10.0.13. The original integration scenarios continue to pass through the same HTTP contracts.

## Post-observability short capacity run

The 60-second workload was rerun after five seconds of warm-up with two Kestrel processes on .NET 10.0.13. Instrumentation, parent-based sampling at ratio 1, the Prometheus exporter, and operational polling were enabled. OTLP network export and console tracing were disabled; the load runner sets logs to Warning. Thus this measures the instrumented application, **not** the capacity or overhead of an attached telemetry backend or Information-level request logs.

| Measurement | Result |
| --- | --- |
| Successful reads / distinct new bookings | 60,000 / 1,020 |
| Failed read or booking requests / dropped arrivals | 0 / 0 |
| Elapsed including response drain | 60.0146 seconds |
| Read / booking throughput including drain | 999.76/s / 16.996/s |
| Confirmation p50 / p95 / p99 | 6.42 / 8.50 / 10.14 ms |
| Persisted overlapping reservations | 0 |
| Notification backlog at final sample | 0 pending/processing items |

All scheduled target arrivals completed. See [observability run raw result](performance/observability-60s.json). Small latency differences between these local runs are not evidence of a performance improvement. This remains a short synthetic workload, with no sustained collector-load, HA/failover, or monthly availability validation.

## Post-architecture-refactor short capacity run

The same 60-second workload, after five seconds of warm-up, was rerun against two freshly started Kestrel processes with the four-layer implementation. The same local dataset and traffic mix described below were used. Runtime was .NET 10.0.13 with twelve logical processors. Domain/project separation did not require an API or database-schema change.

| Measurement | Result |
| --- | --- |
| Successful reads / distinct new bookings | 60,000 / 1,020 |
| Failed read or booking requests / dropped arrivals | 0 / 0 |
| Elapsed including response drain | 60.0052 seconds |
| Read / booking throughput including drain | 999.91/s / 16.999/s |
| Confirmation p50 / p95 / p99 | 6.25 / 8.97 / 14.18 ms |
| Persisted overlapping reservations | 0 |
| Notification backlog at final sample | 2 pending/processing items |

All scheduled target arrivals completed. See [post-refactor raw result](performance/clean-architecture-60s.json). This remains a short synthetic local run, with no production load balancer, HA/failover exercise, or sustained queue-stability proof.

## Initial short capacity run, before the architecture refactor

Environment: Windows, .NET runtime 10.0.10 / SDK 10.0.302, PostgreSQL 18.6, twelve logical processors. Load generator, database, and two Kestrel processes share the workstation. Each API has a maximum connection pool of twenty. The runner uses loopback HTTP and distributes requests between ports; no load balancer, cross-zone network, production authentication, or external provider is involved. CPU/memory/I/O time series were not captured, so this run does not establish a production sizing recommendation.

Dataset: twenty bay/qualified-technician pairs at one dealership, two customer/vehicle identities, 60- and 75-minute service estimates with ten-minute buffers, dated availability, and configured pricing. Sixty-four appointments prepare the read fixtures. Read traffic is 50% appointment detail, 30% inbox, and 20% reference data. Booking flows create fresh quotes and distinct appointments over future slots. Both notification workers are active.

Command: `dotnet run --project tools/Scheduler.Load -c Release --no-build -- --seconds 60 --warmup 5 --reads 1000 --bookings 17 --replicas 2`.

| Measurement | Result |
| --- | --- |
| Measurement window | 60 seconds after five seconds warm-up |
| Elapsed including final response drain | 60.0046 seconds |
| Successful read requests | 60,000 |
| Unique successful new bookings | 1,020 |
| Total measured HTTP requests including quotes | 62,040 |
| Failed reads / booking workflows | 0 / 0 |
| Dropped / late generator iterations | 0 / 0 |
| Read throughput including drain | 999.92/s |
| Booking throughput including drain | 16.999/s |
| Successful confirmation p50 / p95 / p99 | 6.57 / 8.92 / 10.56 ms |
| Persisted overlapping reservations | 0 |
| Notification backlog at final sample | 3 pending/processing items |

All scheduled arrivals in the 60-second target window completed. The tiny rate difference from 1,000/17 is the final 4.6 ms response drain. Three notification items at a single endpoint sample do not prove backlog stability or drainage; a sustained queue-age time series remains required. See [raw result](performance/target-60s.json).

## Failed exploratory run and generator correction

An earlier run requested the same 60-second workload but reported 763.79 seconds including drain, 14,789 dropped iterations, 117 booking workflow failures, and one read failure. Its 45,210 reads and 903 bookings do not establish target capacity. There were zero persisted overlaps. See [raw failed result](performance/exploratory-failed.json).

The original scheduler could replay overdue arrivals in a burst and had no scheduling-lag/failure-category diagnostics. It was changed to drop arrivals more than 100 ms late, report those explicitly, categorize request failures, and reject runs with excessive completion drain. The exact cause of the original prolonged elapsed time was not established. A subsequent diagnostic at 100 reads/s plus 17 bookings/s completed without errors or dropped arrivals, followed by the successful target run above. Do not present the first result as a valid throughput baseline or attribute its cause solely to the API.

## Not yet verified

- The planned five-minute warm-up / 30-minute sustained workload, realistic larger customer/dealership mixes, and production-scale dataset history.
- Capacity after instance loss, database failover, zone loss, recovery time, durable replication, restore exercises, or monthly 99.9% uptime.
- Docker/Compose execution on this workstation, where Docker is unavailable.
- Actual external-provider idempotency/delivery behavior, production identity, authentication UI, online payment, recovery proposal/support allocation workflows, or real reminders.

The GitHub Actions workflow is included; its remote execution status must be checked separately after publication. The applicant's code review and 5–10 minute video remain submission tasks.
