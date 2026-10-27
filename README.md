# Appointment Scheduler

Scenario A of the Keyloop assessment: a .NET 10 backend that quotes a vehicle service, reserves a qualified technician and a bay for the entire interval, and persists the appointment in PostgreSQL. The client is represented by OpenAPI, an executable demo, and HTTP integration tests.

## Delivered scope

- Web booking with immutable ten-minute quotes, service/vehicle duration estimates, explicit buffers, configured peak/off-peak pricing, and payment at check-in.
- Atomic booking and outbox persistence, PostgreSQL exclusion constraints, resource row locks, bounded booking retries, and one appointment per quote.
- Customer-owned reads and cancellation before work starts. Cancellation releases planned capacity, preserves history, and emits notifications.
- Staff start/progress/completion commands, actual occupancy, downstream overrun incidents, stale-forecast protection, and revision checks. No customer appointment is silently moved.
- Durable notification outbox, inbox, mock delivery provider, competing workers, leases, retry/backoff, failed-delivery inspection/retry, and suppression of unsent confirmations after cancellation.
- PostgreSQL integration tests across two API hosts, correlated JSON logs, OpenTelemetry export, Prometheus metrics, health endpoints, and an isolated load runner.
- Booking-to-notification traces across the durable outbox, database timing, operational gauges, and optional local Aspire/Prometheus viewing with alert rules.
- Bounded `IMemoryCache` for shared reference data and pricing/duration policy snapshots, with absolute expiry, coalesced loads, and cache telemetry.

## Clean Architecture and domain model

The backend has four production projects with enforced dependency boundaries:

| Project | Responsibility |
| --- | --- |
| `Scheduler.Domain` | `ServiceAppointment` aggregate, owned `WorkExecution` entity, `BookingOffer`, validated `ServiceInterval`/`Money`/`ServiceEstimate` value objects, overrun policy, and typed domain events. No framework, persistence, or other project dependencies. |
| `Scheduler.Application` | Booking/operations/notification use cases, authorization, contracts, and interfaces for persistence, delivery, and reference reads. Depends on Domain. |
| `Scheduler.Infrastructure` | PostgreSQL repositories and transaction implementation, SQL migrations, outbox/delivery storage, mock provider, and background host adapter. Implements Application interfaces. |
| `Scheduler.Api` | HTTP endpoints, input serialization, Problem Details mapping, demo identity, and dependency injection composition. |

Lifecycle changes go through aggregate methods rather than writable entity properties or SQL decisions. Domain events are converted into outbox rows in the same transaction as the business change. Resource allocation still uses shared database locks and exclusion constraints because conflicts span appointments and API instances. Immutable persistence snapshots and HTTP DTOs are separate from the behavior-owning aggregate. See the [architecture and DDD guide](docs/architecture.md).

Support staffing, customer reschedule proposals, automatic recovery, reminders, real email/SMS, native mobile, online payment, and production authentication are **design extensions**, not implemented endpoints. Staff judge whether more people would shorten work. This application records forecasts; it does not predict productivity. Production HA infrastructure is documented, not provisioned.

## Run locally

Prerequisites: .NET 10 SDK and PostgreSQL 18 with `btree_gist` available. Docker is optional. Commands below run from the repository root.

Create an empty database named `appointment_scheduler` using your local PostgreSQL administration tool. The migration account needs permission to create tables and the `btree_gist` extension. Runtime deployments should use a separate restricted role; local demonstration may use the database owner.

In PowerShell, configure the connection in the current session. Substitute your local credentials; no actual password is stored in this repository.

```powershell
$env:ConnectionStrings__Scheduler = 'Host=127.0.0.1;Port=5432;Database=appointment_scheduler;Username=YOUR_USER;Password=YOUR_PASSWORD;Maximum Pool Size=20;Timeout=5'
$env:ASPNETCORE_ENVIRONMENT = 'Development'
dotnet restore
dotnet build -c Release --no-restore
dotnet run --project src/Scheduler.Api -c Release --no-build -- --migrate --seed
dotnet run --project src/Scheduler.Api -c Release --no-build -- --urls http://127.0.0.1:5080
```

In another terminal:

```powershell
./scripts/demo.ps1 -BaseUrl http://127.0.0.1:5080
```

Swagger UI: `http://127.0.0.1:5080/swagger`. Click **Authorize**, enter `customer-a`, and use **Try it out** to send customer requests. Use `staff` for workshop operations. OpenAPI JSON remains at `/openapi/v1.json`; liveness and database readiness are `/health/live` and `/health/ready`. The migration command is repeatable and records applied embedded SQL files; it never resets existing data. Demo availability covers dated 08:00–18:00 local intervals for the next year starting at initial seeding. Refresh dated seed data explicitly for an old demonstration database.

Demo identity uses the `X-Demo-Actor` header with `customer-a`, `customer-b`, or `staff`. Customer access is limited to owned records and staff access to the seeded dealership. **These headers are impersonatable test identities, not production authentication.** The application refuses to serve requests outside Development/Testing until real authentication is implemented. Keep the demo bound to localhost.

### Optional Docker Compose

```powershell
# A disposable local database password, not an existing account password.
$env:POSTGRES_PASSWORD = 'choose-a-local-demo-password'
docker compose up --build
```

The API is on localhost:5080 and PostgreSQL on localhost:5433. A one-off migration service runs before the API; the named volume persists data. This full-stack Compose configuration has not been exercised locally; the dashboard-only Compose setup below has been verified.

## Observe the application

JSON console logs and `GET /metrics` are enabled by default. Responses expose `X-Trace-Id`; use it to correlate request, booking, database, and asynchronous notification activity. Set `Observability__ConsoleTracing=true` for local span output, or `Observability__OtlpEndpoint=http://127.0.0.1:4318` for OTLP/HTTP export of logs, metrics, and traces. Run migrations before starting the updated API so existing outbox rows receive the nullable trace-context columns.

For the API/PostgreSQL already running on the host, `docker compose -f compose.telemetry.yaml up -d` starts Aspire at localhost:18888 and Prometheus at localhost:9090. This setup has been verified with real logs, traces, metrics, and a healthy scrape target. Configure the API's OTLP endpoint and Docker host allowlist as shown in the [observability walkthrough](docs/observability.md). For the separate full-stack deployment, the override remains `docker compose -f compose.yaml -f compose.observability.yaml up --build`.

## Caching

Shared dealership/service data is cached for five minutes; pricing, time bands, and duration rules are cached together for one minute. Vehicle ownership/category and booking availability remain PostgreSQL reads. Each process has its own cache; direct configuration updates become visible after expiry or explicit local invalidation. Redis is a future shared-cache option for multiple replicas. See [cache behavior, configuration, and Redis plan](docs/caching.md).

## Test

Tests create and remove their own uniquely named `scheduler_test_<GUID>` database. Set an administration connection with CREATEDB and extension permissions; never point tests at a production server.

```powershell
$env:SCHEDULER_TEST_ADMIN = 'Host=127.0.0.1;Port=5432;Database=postgres;Username=YOUR_USER;Password=YOUR_PASSWORD;Timeout=5'
dotnet test -c Release
```

Database tests fail with setup instructions if this variable is missing; they are not silently skipped. The suite verifies real PostgreSQL semantics, including bay-only and technician-only conflicts, independent API instances, last-slot races, same-quote replay, atomic rollback when outbox insertion fails, cancellation/start races, both booking/forecast commit orders, actual overruns, expired worker claims, and provider recovery. See [validation results](docs/validation.md) for the recorded run.

Tests follow **Given–When–Then** scenarios using xUnit and Fluent Assertions 7.2.0. Names describe the precondition, business action, and expected outcome. Each test separates those phases explicitly; related outcomes use `AssertionScope`, and scenario outlines use theories. The suite currently has **91 passing examples**: 24 domain/rule examples, six cache unit examples, four architecture checks, and 57 PostgreSQL/API/observability/cache integration scenarios. See the [testing convention](docs/testing.md) before adding tests.

Domain, cache unit, and architecture tests run without a database connection:

```powershell
dotnet test --filter 'FullyQualifiedName~DomainScenarios|FullyQualifiedName~RulesTests|FullyQualifiedName~ArchitectureScenarios|FullyQualifiedName~MemoryCacheScenarios'
```

## Performance

The applicant's targets are **1,000 reads/s concurrently with 17 new booking commits/s**, with proposed booking confirmation p95 <= 2 seconds and p99 <= 3 seconds. Quotes and notifications add writes. Monthly availability target: >=99.9%. These are requirements, not guarantees based on architecture alone.

```powershell
# Uses SCHEDULER_TEST_ADMIN and the Release build from the commands above.
dotnet run --project tools/Scheduler.Load -c Release --no-build -- --seconds 60 --warmup 5 --reads 1000 --bookings 17 --replicas 2
# Longer planned validation:
dotnet run --project tools/Scheduler.Load -c Release --no-build -- --seconds 1800 --warmup 300 --reads 1000 --bookings 17 --replicas 2
```

The runner creates `scheduler_load_<GUID>`, seeds twenty bay/qualified-technician pairs, launches real Kestrel processes without visible windows, schedules independent arrival streams, and cleans up its processes/database on normal completion or exception. Results are written to ignored `artifacts/load-*.json`. The read mix is 50% appointment detail, 30% inbox, 20% reference data across two demo identities. Bookings alternate 60/75-minute service profiles, add ten-minute buffers, and spread over future days. Notification workers run throughout. Calls are distributed directly across API ports; there is no production load balancer. Requests have a ten-second client timeout and generator concurrency is bounded. Report this synthetic workload and machine constraints with any result.

Do not equate a short local run with the planned 30-minute sustained test, production capacity, database failover, or monthly uptime. See [system design](docs/system-design.md) and [validation results](docs/validation.md).

## API and policy notes

See [API examples](docs/api.md). Write requests use server-side quote terms, UTC instants with explicit offsets, and authenticated ownership. Quotes hold no resources. Valid unexpired quotes survive pricing-policy changes. Accepted prices stay fixed during overruns, and no payment is inferred from confirmation or completion. The seeded USD amounts are illustrative, tax-inclusive final prices for this demo; there is no tax engine.

Cancellation has no fee or advance-notice cutoff while Confirmed and before work starts. Start requires the full work estimate to fit the reserved window; the buffer may absorb a small late start. Completion frees actual occupancy but leaves the original scheduled reservation until its end. Staff must address unresolved late appointments; automated reallocation is deferred.

Notifications are at least once at an external-provider boundary. `ProviderAccepted` is neither delivered/read nor customer consent. The current provider only logs delivery IDs. No messages are sent to real customers.

## AI Collaboration Narrative

The applicant chose Scenario A, .NET, PostgreSQL, and a five-day backend scope. During design review, they challenged fixed durations with a 75-minute inspection, rejected moving B without consent, assigned productivity judgments to workshop staff, requested notifications, and specified web-only booking, pay-at-check-in, cancellation, configured pricing, and numerical performance requirements.

AI translated those decisions into explicit invariants, a schema and API, PostgreSQL locking/constraints, and implementation/tests. The implementation uses Npgsql/Dapper and embedded SQL migrations instead of the draft's EF Core proposal so the resource locks and PostgreSQL range constraints are directly inspectable. This tradeoff reduces ORM machinery but leaves more SQL and mapping maintenance with the application.

Verification used actual compilation and PostgreSQL tests rather than an in-memory substitute. NuGet restore rejected a vulnerable transitive OpenAPI version; the dependency was upgraded to the patched 2.7.5 release. A deliberately injected outbox constraint failure verified rollback of the appointment and both reservations. Tests use separate application hosts for booking races and worker competition. Recorded performance evidence is kept separate from targets and production claims.

During implementation review, the applicant requested BDD-style tests and Fluent Assertions. AI reorganized the suite into focused Given–When–Then scenarios, split combined behaviors into independent examples, and preserved the real PostgreSQL race/failure checks. All 51 resulting examples passed. The increase from 31 primarily reflects clearer scenario boundaries, not a claim of twenty entirely new business requirements.

The applicant then challenged the lack of Clean Architecture and DDD. AI separated four production projects, moved lifecycle behavior into the appointment aggregate and its execution entity, introduced value objects and typed domain events, and implemented application-owned persistence interfaces in Infrastructure. The original 51 scenarios still pass, with 23 additional domain, architecture, and migration checks. Dependency tests prevent inward layers from importing HTTP or database adapters. Migration resource identities were deliberately retained to avoid rerunning the schema on existing databases. A new architecture-test assertion initially failed compilation because expression trees do not support its pattern syntax; the assertion was corrected before the full suite passed.

The applicant then identified that logs, activities, and meters alone did not complete observability. AI added the SDK/exporters, JSON correlation, metrics scraping, persisted asynchronous trace context, database spans, operational snapshots, and optional local viewers. Six BDD scenarios verified actual three-signal HTTP export, cross-worker parentage, retries, stale forecasts, legacy events, collector failure isolation, and sampling. The tests caught late collector configuration in the test host and a mistaken assumption that the Prometheus pull reader could be force-flushed; both were corrected before all 80 examples passed. After the applicant installed Docker Desktop, the local Aspire/Prometheus setup was verified with a real booking and notification trace, exported logs, and scraped metrics. Performance evidence still separates instrumentation from attached-collector throughput.

The applicant also requested `IMemoryCache` for reference/pricing/duration data and a Redis improvement plan. Infrastructure now caches shared catalogue data and coherent quote-policy snapshots while rechecking vehicle ownership/category and keeping resource allocation in PostgreSQL. Eleven new BDD examples verify expiry, concurrent fills, invalidation, failure recovery, and isolation. All 91 examples passed, and the repeated short target-rate workload completed without failures or overlaps. Cache telemetry verifies source-load reuse; the result is not presented as a measured latency improvement or a Redis implementation.

The applicant still needs to review and explain the code and tradeoffs, run the demonstration, and record their own assessment video. This narrative records actual collaboration and verification; it does not claim the applicant has already reviewed every generated implementation detail. Design-phase collaboration is separately covered in [system design](docs/system-design.md).
