# Appointment Scheduler

Scenario A of the Keyloop assessment: a .NET 10 backend that quotes a vehicle service, reserves a qualified technician and a bay for the entire interval, and persists the appointment in PostgreSQL. The client is represented by OpenAPI, an executable demo, and HTTP integration tests.

## Delivered scope

- Web booking with immutable ten-minute quotes, service/vehicle duration estimates, explicit buffers, configured peak/off-peak pricing, and payment at check-in.
- Atomic booking and outbox persistence, PostgreSQL exclusion constraints, resource row locks, bounded booking retries, and one appointment per quote.
- Customer-owned reads and cancellation before work starts. Cancellation releases planned capacity, preserves history, and emits notifications.
- Staff start/progress/completion commands, actual occupancy, downstream overrun incidents, stale-forecast protection, and revision checks. No customer appointment is silently moved.
- Durable notification outbox, inbox, mock delivery provider, competing workers, leases, retry/backoff, failed-delivery inspection/retry, and suppression of unsent confirmations after cancellation.
- PostgreSQL integration tests across two API hosts, structured logs, activities/meters, health endpoints, and an isolated load runner.

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

OpenAPI JSON: `http://127.0.0.1:5080/openapi/v1.json`. Liveness and database readiness: `/health/live` and `/health/ready`. The migration command is repeatable and records applied embedded SQL files; it never resets existing data. Demo availability covers dated 08:00–18:00 local intervals for the next year starting at initial seeding. Refresh dated seed data explicitly for an old demonstration database.

Demo identity uses the `X-Demo-Actor` header with `customer-a`, `customer-b`, or `staff`. Customer access is limited to owned records and staff access to the seeded dealership. **These headers are impersonatable test identities, not production authentication.** The application refuses to serve requests outside Development/Testing until real authentication is implemented. Keep the demo bound to localhost.

### Optional Docker Compose

```powershell
# A disposable local database password, not an existing account password.
$env:POSTGRES_PASSWORD = 'choose-a-local-demo-password'
docker compose up --build
```

The API is on localhost:5080 and PostgreSQL on localhost:5433. A one-off migration service runs before the API; the named volume persists data. This Compose configuration has not been exercised in the current workstation environment because Docker is unavailable.

## Test

Tests create and remove their own uniquely named `scheduler_test_<GUID>` database. Set an administration connection with CREATEDB and extension permissions; never point tests at a production server.

```powershell
$env:SCHEDULER_TEST_ADMIN = 'Host=127.0.0.1;Port=5432;Database=postgres;Username=YOUR_USER;Password=YOUR_PASSWORD;Timeout=5'
dotnet test -c Release
```

Database tests fail with setup instructions if this variable is missing; they are not silently skipped. The suite verifies real PostgreSQL semantics, including bay-only and technician-only conflicts, independent API instances, last-slot races, same-quote replay, atomic rollback when outbox insertion fails, cancellation/start races, both booking/forecast commit orders, actual overruns, expired worker claims, and provider recovery. See [validation results](docs/validation.md) for the recorded run.

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

The applicant still needs to review and explain the code and tradeoffs, run the demonstration, and record their own assessment video. This narrative records actual collaboration and verification; it does not claim the applicant has already reviewed every generated implementation detail. Design-phase collaboration is separately covered in [system design](docs/system-design.md).
