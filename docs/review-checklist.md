# Pair review and submission checklist

1. Trace API -> Application use case -> `ServiceAppointment` domain behavior -> Infrastructure repository. Explain the dependency direction, aggregate boundary, immutable snapshots, and typed events. Then trace `BookingService.Book`, the resource lock implementation, and `reservation_no_overlap`; explain why a normal read-then-insert or process-local lock is insufficient across instances.
2. Walk through independent quotes racing for the final pair and the same quote racing twice. Explain 201 versus replay 200 and how to recover when the response is lost after commit.
3. Review price/duration snapshots, local-time pricing boundaries, PayAtCheckIn, and why no payment is marked collected.
4. Review cancellation/start serialization and the injected-outbox-failure test. Confirm that cancellation releases only the intended reservation rows.
5. Walk through A's overrun, B's unchanged commitment, stale forecasts, and late completion. Explain why an occupied resource and an insufficient remaining reservation each block B independently.
6. Review notification leases, token-guarded acknowledgement, at-least-once semantics, retry/failure visibility, and why a delivered proposal could never count as consent in the future recovery extension.
7. Discuss mock authentication, SQL migrations versus EF Core, the single-writer bottleneck, pool budgeting, and the difference between the local short benchmark and production availability.
8. Run the README from a clean checkout, inspect test results, and review all declared extensions before submission.

Suggested eight-minute video: introduction/scenario (30s), architecture and assumptions (90s), concurrency and failure behavior (90s), executable API demo (120s), actual AI collaboration/corrections (90s), limitations and tradeoffs (60s). The applicant should record and explain the work in their own words. The application does not require a runtime AI agent.
