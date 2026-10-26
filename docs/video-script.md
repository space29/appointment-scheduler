Video script for the Keyloop assessment

Aim for roughly nine minutes, including short screen transitions. The final length depends on your speaking pace; rehearse once and keep the recording below ten minutes. The AI collaboration section is intended to take 90 seconds. Italic screen cues are directions, not words to read aloud. Replace the name placeholder and adjust wording to match your own voice.

Before recording, open the system diagram, solution folders, API request examples, test results, observability code, and recorded benchmark. Configure the database connection off camera. Prepare quote, confirm, replay, and cancel requests from `docs/api.md`, using Swagger UI if preferred. The existing `scripts/demo.ps1` performs this sequence if your shell permits local scripts. Rehearse the requests against a free future slot. Use recorded results when explaining completed tests; only say you are running something live when you actually do so. Aspire and Prometheus have been verified locally; open their pages before recording the observability segment.

**0:00–0:35 — Introduction and scope**

*Show the repository README.*

“Hi, I’m [your name]. This is my solution for Scenario A of the Keyloop coding challenge.

I focused on the backend using .NET and PostgreSQL. The main requirement is to book a vehicle service only when a qualified technician and a bay are available for the full appointment.

Beyond the core brief, I considered workshop overruns, cancellation, notifications, and configured peak and off-peak pricing. I’ll explain the main decisions, demonstrate the booking flow, and describe how I worked with AI.”

**0:35–1:25 — Architecture**

*Show the component diagram in `docs/system-design.md`, then the four production projects.*

“The solution has four layers: Domain, Application, Infrastructure, and API.

Domain owns the appointment lifecycle and business rules. Application coordinates use cases such as booking, cancellation, and reporting progress. Infrastructure implements PostgreSQL persistence and notification delivery. API handles HTTP and connects the components.

The appointment is the aggregate root, and its work execution tracks what actually happens in the workshop. Value objects validate money, time intervals, and service estimates.

I kept this as one deployable application. For a five-day assignment, that keeps the system understandable and lets a booking and its notification event commit in one database transaction.”

**1:25–2:30 — Booking correctness and concurrency**

*Show `BookingService.Book`, then the reservation exclusion constraint in `001_initial.sql`.*

“The difficult part is handling concurrent requests for the same resources.

A request first obtains a quote containing the estimated duration, buffer, and price. A quote does not reserve capacity.

During confirmation, the application finds candidate resources, locks them in a consistent order, and checks availability again. PostgreSQL also has an exclusion constraint that prevents overlapping active reservations for the same resource.

This protects the booking even when requests reach different application instances.

The appointment, its resource reservations, and its outbox event commit together. Confirmation is returned after the commit.

The quote identifier also makes confirmation idempotent. If the server commits but the response is lost, the customer can retry the same quote and receive the existing appointment. That avoids creating a second booking.”

**2:30–4:00 — API demonstration**

*Show prepared API requests and responses. Pause briefly at each result.*

“Here I request a quote for a vehicle, service, dealership, and start time.

The response includes the work estimate, buffer, and accepted price. These values are captured in the quote, so a later pricing configuration change does not silently change a valid offer.

Now I confirm using the quote identifier. The first response creates the appointment, with a bay and a qualified technician allocated for the complete interval.

I submit the same confirmation again. It returns the same appointment identifier, demonstrating safe replay.

Payment currently defaults to pay at check-in. This records the payment arrangement; it does not claim that payment has been collected.

Next, I cancel before work starts. The appointment history remains, and its planned reservations are released.

The notification worker processes the committed events asynchronously. The provider is mocked for this assessment, and the inbox exposes the resulting notifications.”

**4:00–4:50 — A realistic workshop overrun**

*Show `OperationsService.Progress` and an overrun scenario in `OperationsScenarios.cs`.*

“A service estimate is not a guarantee of actual duration. For example, an inspection planned for sixty minutes might take seventy-five.

The system separates the accepted appointment from actual workshop execution. Staff can report a revised completion forecast. The application identifies affected appointments, records an incident, and notifies staff.

It does not move the next customer’s appointment automatically. An unfinished job continues to occupy its resources, even if its forecast has expired.

Additional staffing and customer-approved rescheduling are documented extensions. Staff would judge whether extra help could shorten the work; the scheduler would validate resource availability and preserve customer consent.”

**4:50–6:00 — Testing and observability**

*Show the recorded 80-test result, a Given–When–Then scenario, `Observability.cs`, and a metrics response or correlated log.*

“The current suite has eighty passing examples. Tests use Given–When–Then scenarios with Fluent Assertions.

The integration tests use real PostgreSQL. They cover competing bookings, replay, cancellation races, notification retries, and transaction rollback when writing the outbox fails. Architecture tests also enforce dependency direction.

For observability, the API emits structured JSON logs, Prometheus metrics, and OpenTelemetry traces.

A response trace ID connects the request to booking and database activity. Trace context is saved in the outbox, allowing a later notification attempt on another worker to continue the same trace.

Metrics include booking duration, outcomes, delivery failures, and stale forecasts. Tests verify actual telemetry export and that a collector outage does not undo a successful booking.

The local Aspire and Prometheus dashboards have also been verified with a real booking trace, exported logs, and scraped metrics.”

**6:00–6:45 — Performance evidence and scaling**

*Show the post-observability results in `docs/validation.md`.*

“I also set a target of one thousand reads and seventeen new bookings per second, with booking confirmation within two to three seconds.

In the recorded sixty-second local test, two API processes completed sixty thousand reads and one thousand and twenty new bookings. There were no failed requests, dropped arrivals, or overlapping reservations. Booking p99 was about ten milliseconds.

That is a short synthetic result. OTLP export was disabled and logging was set to Warning. It does not prove production capacity or 99.9 percent uptime.

API instances can scale horizontally, while the shared PostgreSQL writer still needs capacity planning and failover validation.”

**6:45–8:15 — AI collaboration**

*Show the README AI Collaboration Narrative and the relevant conversation examples, if available.*

“I used AI to help analyze the brief, compare design options, and generate implementation, tests, and documentation.

My contribution included choosing the backend scope and technology stack, challenging assumptions, and setting review criteria.

One important discussion concerned duration. I questioned the initial fixed-duration model with an inspection that takes longer than expected. That led to separating planned estimates from actual execution and completion forecasts.

I also challenged a recovery proposal that moved the next customer’s appointment. My concern was customer consent. The resulting design preserves that customer’s commitment and records the disruption for staff to address.

During implementation review, I requested BDD-style tests with Fluent Assertions and clearer Clean Architecture and domain boundaries.

I then identified that basic log calls and meters were not enough. That review led to configured telemetry export, asynchronous trace correlation, and operational metrics.

AI generated substantial code, and the work was checked through compilation, real database tests, and load tests. The exporter tests caught configuration and SDK assumptions that had to be corrected.

For me, the useful part of AI collaboration was making assumptions visible and testing them. The generated output still needs engineering review, and I remain responsible for understanding and explaining the solution.”

**8:15–9:00 — Tradeoffs and next steps**

*Return to the README scope and extension notes.*

“The main tradeoffs are explicit. PostgreSQL locking and constraints provide strong booking correctness, while explicit SQL adds mapping and maintenance work.

The outbox separates notification delivery from booking confirmation. External delivery is at least once, so a real provider integration must handle possible duplicates.

The implemented scope includes booking, cancellation, execution tracking, configured pricing, and mock notifications. Production authentication, online payment, real messaging providers, and consent-based recovery remain future work.

My next steps would be longer load testing with telemetry export enabled, database failover testing, and implementing the customer-consent workflow.

Thank you for reviewing my solution. I’m happy to discuss the concurrency decisions, domain model, and tradeoffs in more detail.”

If rehearsal runs long, shorten the architecture paragraph about value objects and the list of metrics. Keep the booking demonstration, concurrency explanation, explicit implementation limits, and the 1–2 minute AI collaboration section.
