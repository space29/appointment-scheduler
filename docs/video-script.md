Video script for the Keyloop assessment

Aim for roughly nine and a half minutes, including short screen transitions. The final length depends on your speaking pace; rehearse once and keep the recording below ten minutes. The AI collaboration section is intended to take 90 seconds, followed by a one-minute reflection on learning and challenges. Italic screen cues are directions, not words to read aloud. Replace the name placeholder and adjust wording to match your own voice and experience.

Before recording, open the system diagram, solution folders, API request examples, test results, observability code, and recorded benchmark. Configure the database connection off camera. Prepare quote, confirm, replay, and cancel requests from `docs/api.md`, using Swagger UI if preferred. The existing `scripts/demo.ps1` performs this sequence if your shell permits local scripts. Rehearse the requests against a free future slot. Use recorded results when explaining completed tests; only say you are running something live when you actually do so. Aspire and Prometheus have been verified locally; open their pages before recording the observability segment.

**0:00–0:35 — Introduction and scope**

*Show the repository README.*

“Hi, I’m [your name]. This is my solution for Scenario A of the Keyloop coding challenge.

I focused on the backend using .NET and PostgreSQL. The main requirement is to book a vehicle service only when a qualified technician and a bay are available for the full appointment.

Beyond the core brief, I considered workshop overruns, cancellation, notifications, and configured peak and off-peak pricing. I’ll explain the main decisions, demonstrate the booking flow, and describe how I worked with AI.”

**0:35–1:45 — Architecture**

*Show the first component diagram in `docs/system-design.md`. Point from left to right: web client → API → quote/booking components → PostgreSQL → notification worker. Then point to operations and telemetry.*

“This diagram shows how a booking moves through the system.

The web client calls the API. Quotes calculate the duration, buffer, and configured price.

Reference data and pricing/duration rules use memory caching with short expiry. Booking availability remains authoritative in PostgreSQL.

When the customer confirms, the booking component allocates a qualified technician and a bay for the entire interval. PostgreSQL stores the appointment and enforces reservation constraints.

The transaction also saves an outbox event. A background worker processes it asynchronously to create notifications, so confirmation does not depend on the delivery provider.

Operations tracks workshop execution. The API and worker emit logs, metrics, and traces.”

*Switch to the solution folders and point to each project as you name its responsibility.*

“These components run within one application, organized into four projects.

Domain owns business rules. Application coordinates use cases. Infrastructure implements persistence and delivery. API handles HTTP and connects the components.”

**1:45–2:45 — Booking correctness and concurrency**

*Show `BookingService.Book`, then the reservation exclusion constraint in `001_initial.sql`.*

“The difficult part is handling concurrent requests for the same resources.

A quote captures duration and price, but does not reserve capacity.

During confirmation, the application finds candidate resources, locks them in a consistent order, and checks availability again. PostgreSQL also has an exclusion constraint that prevents overlapping active reservations for the same resource.

This protects the booking even when requests reach different application instances.

The appointment, its resource reservations, and its outbox event commit together. Confirmation is returned after the commit.

The quote identifier also makes confirmation idempotent. If the server commits but the response is lost, the customer can retry the same quote and receive the existing appointment. That avoids creating a second booking.”

**2:45–4:15 — API demonstration**

*Show prepared API requests and responses. Pause briefly at each result.*

*Use the endpoint cue cards at the end of this document when explaining a specific Swagger operation. They are presenter reference notes; choose the lines relevant to the demonstration within this time slot.*

“Here I request a quote for a vehicle, service, dealership, and start time.

The response includes the work estimate, buffer, and accepted price. These values are captured in the quote, so a later pricing configuration change does not silently change a valid offer.

Now I confirm using the quote identifier. The first response creates the appointment, with a bay and a qualified technician allocated for the complete interval.

I submit the same confirmation again. It returns the same appointment identifier, demonstrating safe replay.

Payment currently defaults to pay at check-in. This records the payment arrangement; it does not claim that payment has been collected.

Next, I cancel before work starts. The appointment history remains, and its planned reservations are released.

The notification worker processes the committed events asynchronously. The provider is mocked for this assessment, and the inbox exposes the resulting notifications.”

**4:15–5:05 — A realistic workshop overrun**

*Show `OperationsService.Progress` and an overrun scenario in `OperationsScenarios.cs`.*

“A service estimate is not a guarantee of actual duration. For example, an inspection planned for sixty minutes might take seventy-five.

The system separates the accepted appointment from actual workshop execution. Staff can report a revised completion forecast. The application identifies affected appointments, records an incident, and notifies staff.

It does not move the next customer’s appointment automatically. An unfinished job continues to occupy its resources, even if its forecast has expired.

Additional staffing and customer-approved rescheduling are documented extensions. Staff would judge whether extra help could shorten the work; the scheduler would validate resource availability and preserve customer consent.”

**5:05–6:15 — Testing and observability**

*Show the recorded 91-test result, a Given–When–Then scenario, `Observability.cs`, and a metrics response or correlated log.*

“The current suite has ninety-one passing examples. Tests use Given–When–Then scenarios with Fluent Assertions.

The integration tests use real PostgreSQL. They cover competing bookings, replay, cancellation races, notification retries, and transaction rollback when writing the outbox fails. Architecture tests also enforce dependency direction.

For observability, the API emits structured JSON logs, Prometheus metrics, and OpenTelemetry traces.

A response trace ID connects the request to booking and database activity. Trace context is saved in the outbox, allowing a later notification attempt on another worker to continue the same trace.

Metrics include booking duration, outcomes, delivery failures, and stale forecasts. Tests verify actual telemetry export and that a collector outage does not undo a successful booking.

The local Aspire and Prometheus dashboards have also been verified with a real booking trace, exported logs, and scraped metrics.”

**6:15–7:00 — Performance evidence and scaling**

*Show the post-caching short-run results in `docs/validation.md`.*

“I also set a target of one thousand reads and seventeen new bookings per second, with booking confirmation within two to three seconds.

In the recorded sixty-second local test, two API processes completed sixty thousand reads and one thousand and twenty new bookings. There were no failed requests, dropped arrivals, or overlapping reservations. Booking p99 was about eleven milliseconds.

That is a short synthetic result. OTLP export was disabled and logging was set to Warning. It does not prove production capacity or 99.9 percent uptime.

API instances can scale horizontally, while the shared PostgreSQL writer still needs capacity planning and failover validation.”

**7:00–8:30 — AI collaboration**

*Show the README AI Collaboration Narrative and the relevant conversation examples, if available.*

“I used AI to help analyze the brief, compare design options, and generate implementation, tests, and documentation.

My contribution included choosing the backend scope and technology stack, challenging assumptions, and setting review criteria.

One important discussion concerned duration. I questioned the initial fixed-duration model with an inspection that takes longer than expected. That led to separating planned estimates from actual execution and completion forecasts.

I also challenged a recovery proposal that moved the next customer’s appointment. My concern was customer consent. The resulting design preserves that customer’s commitment and records the disruption for staff to address.

During implementation review, I requested BDD-style tests with Fluent Assertions and clearer Clean Architecture and domain boundaries.

I then identified that basic log calls and meters were not enough. That review led to configured telemetry export, asynchronous trace correlation, and operational metrics.

AI generated substantial code, and the work was checked through compilation, real database tests, and load tests. The exporter tests caught configuration and SDK assumptions that had to be corrected.

For me, the useful part of AI collaboration was making assumptions visible and testing them. The generated output still needs engineering review, and I remain responsible for understanding and explaining the solution.”

**8:30–9:30 — What I learned and challenges I faced**

*Show the overrun design, then a booking trace in Aspire. Speak about your experience rather than reading code.*

“The main design challenge was handling an overrun without breaking the next customer's agreed appointment. That led me to separate the accepted reservation, actual resource occupancy, and the latest completion forecast.

Another challenge was validating observability end to end. Logs and metrics needed working exporters, trace context across the outbox, and dashboards receiving actual application data.

What I learned is to define a business guarantee precisely, then gather evidence for it. For booking correctness, that meant database constraints and concurrent PostgreSQL tests. For performance, it meant distinguishing a short local result from sustained capacity and availability.

Working with AI also reinforced the value of specific review questions. Challenging duration, customer consent, and missing telemetry led to concrete improvements.”

**9:30–9:50 — Tradeoffs and next steps**

*Return to the README scope and extension notes.*

“SQL requires mapping maintenance, and notification delivery must handle duplicates.

Redis is a future option for shared caching across instances. Other next steps include production integrations, consent-based recovery, sustained load testing, and failover testing.

Thank you for reviewing my solution.”

If rehearsal runs long, shorten the project-folder explanation and the list of metrics. Keep the booking demonstration, concurrency explanation, explicit implementation limits, the 1–2 minute AI collaboration section, and the reflection on learning and challenges. The timestamps are rehearsal targets; adjust the pace after a timed practice run.

**Presenter reference — what each endpoint does**

These are ready-to-say explanations for every exposed API operation and the documentation pages. Use them when pointing to an endpoint in Swagger. This reference is outside the timed narration. Customer requests use `customer-a` or `customer-b`; workshop operations use `staff` in Swagger's Authorize dialog.

| Endpoint | What to say | Caller |
| --- | --- | --- |
| `GET /reference-data` | “This endpoint supplies the reference data needed to prepare a booking, including the caller's vehicles, available service types, dealerships, and supported payment option.” | Customer or staff |
| `POST /appointment-quotes` | “This endpoint creates a temporary offer containing the service estimate, buffer, and price, so the customer can review the terms before booking.” | Customer or staff |
| `POST /appointments` | “This endpoint confirms a quote and reserves a qualified technician and a bay for the entire appointment interval.” | Customer or staff |
| `GET /appointments/{id}` | “This endpoint retrieves the saved appointment, its allocated resources, its work execution if started, and whether it is at risk.” | Owning customer or dealership staff |
| `POST /appointments/{id}/cancel` | “This endpoint cancels a confirmed appointment before work starts, releases its planned capacity, and preserves its history.” | Owning customer or dealership staff |
| `POST /appointments/{id}/start` | “This endpoint records that the workshop has actually started servicing the vehicle.” | Staff |
| `PATCH /appointments/{id}/progress` | “This endpoint records a revised expected completion time and checks whether the work affects other appointments.” | Staff |
| `POST /appointments/{id}/complete` | “This endpoint records that servicing has finished, saves the actual completion time, and releases the resources' actual occupancy.” | Staff |
| `GET /appointments/{id}/impacts` | “This endpoint shows disruption incidents caused by or affecting the appointment, and whether its completion forecast is stale.” | Staff |
| `GET /me/notifications` | “This endpoint returns the current user's notification inbox, including booking confirmations, cancellations, and relevant operational updates.” | Customer or staff, own inbox |
| `POST /me/notifications/{id}/read` | “This endpoint records that the recipient has marked a notification as read.” | Notification recipient |
| `GET /appointments/{id}/notification-deliveries` | “This endpoint lets staff inspect notification delivery attempts, statuses, and recorded errors for an appointment.” | Staff |
| `POST /notification-deliveries/{id}/retry` | “This endpoint queues a failed notification delivery for another attempt after the underlying problem has been addressed.” | Staff |
| `GET /health/live` | “This endpoint confirms that the API process is responding.” | No demo identity required |
| `GET /health/ready` | “This endpoint checks that the API can query PostgreSQL, helping determine whether it is ready to serve requests.” | No demo identity required |
| `GET /metrics` | “This endpoint exposes application and runtime measurements for Prometheus to scrape.” | No demo identity required |
| `GET /openapi/v1.json` | “This endpoint provides the machine-readable API contract used by Swagger UI and other API tools.” | No demo identity required |
| `GET /swagger` | “This page lets me explore the API and execute requests interactively using a selected demo identity.” | No identity needed to open the page; business requests require authorization |

**Details to explain when demonstrating a route**

- A quote lasts ten minutes and holds no resources. Confirmation rechecks availability. A new confirmation returns 201; retrying the same quote returns the same appointment with 200. Confirming records the PayAtCheckIn arrangement, not a payment receipt.
- Starting moves the appointment from Confirmed to InProgress, records the server's actual start time, and creates its work execution. The assigned resources must be free of active work/unresolved impacts, and the remaining reservation must fit the estimated work. Use an appointment in its valid start window; a future appointment cannot be started immediately.
- Start and cancel use the **appointment** revision in `expectedRevision`. Progress and complete use the **execution** revision. Read the latest appointment response to obtain the right revision before changing it.
- Progress requires `expectedCompletion` with an explicit offset and a reason. It updates the forecast and records impacts; it does not automatically move another customer's appointment.
- Completion releases actual occupancy. The original scheduled reservation remains until its end. Completion also reevaluates downstream impacts, which can remain unresolved if the following appointment no longer has enough time.
- Notification delivery is asynchronous. ProviderAccepted is distinct from delivery/read status, and marking a notification read is separate from consenting to a schedule change. Manual retry applies to Failed deliveries and returns 202 to acknowledge the retry request.

**Choose the demonstration flow**

For the timed video, use the existing quote → confirm → retry → read → cancel → inbox sequence. To explain workshop execution, use a **different appointment** and show start → progress → impacts → complete. An appointment cancelled in the first sequence cannot then be started. If the booking is outside its valid start window, show the operational test scenario while explaining these routes. The API uses the real server clock.
