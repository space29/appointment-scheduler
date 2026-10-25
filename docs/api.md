# API review guide

All business endpoints require `X-Demo-Actor: customer-a`, `customer-b`, or `staff` in the local demonstration. Identifiers below come from the seed. Use future dates within seeded availability.

```http
GET http://127.0.0.1:5080/reference-data
X-Demo-Actor: customer-a
```

```http
POST http://127.0.0.1:5080/appointment-quotes
X-Demo-Actor: customer-a
Content-Type: application/json

{
  "customerId": "10000000-0000-0000-0000-000000000001",
  "vehicleId": "20000000-0000-0000-0000-000000000001",
  "dealershipId": "30000000-0000-0000-0000-000000000001",
  "serviceTypeId": "40000000-0000-0000-0000-000000000001",
  "startsAt": "REPLACE_WITH_FUTURE_ISO_TIMESTAMP_WITH_OFFSET"
}
```

Inspect the returned duration, buffer, end, total/currency, pricing band/version, expiry, and payment options before confirming. Quote requests do not allocate resources.

```http
POST http://127.0.0.1:5080/appointments
X-Demo-Actor: customer-a
Content-Type: application/json

{ "quoteId": "REPLACE_WITH_QUOTE_ID", "paymentTiming": "PayAtCheckIn" }
```

A new booking returns 201, a Location header, and `{ appointment, resources, execution, atRisk }`. The same quote returns the existing record with 200, even after quote expiry. Replay after cancellation returns that cancelled record; a new booking requires a new quote. Clients must retain the quote ID across network failures and reconcile through replay. There is no separate payment charge.

| Route | Actor | Body / behavior |
| --- | --- | --- |
| `GET /appointments/{id}` | Owner or dealership staff | Persisted record and resource associations. |
| `POST /appointments/{id}/cancel` | Owner or staff | `{ "expectedRevision": 1, "reason": "Changed plans" }`; appointment revision. |
| `POST /appointments/{id}/start` | Staff | `{ "expectedRevision": 1, "reason": "Checked in" }`; appointment revision. Actual time comes from the server. |
| `PATCH /appointments/{id}/progress` | Staff | `{ "expectedRevision": 1, "expectedCompletion": "...offset...", "reason": "Further inspection" }`; **execution** revision. |
| `POST /appointments/{id}/complete` | Staff | `{ "expectedRevision": 2, "reason": "Work finished" }`; **execution** revision. Actual time comes from the server. |
| `GET /appointments/{id}/impacts` | Staff | Caused/received incidents and stale-forecast status. No customer times are changed. |
| `GET /me/notifications?limit=50` | Customer or staff | Own inbox only, limit 1–100. Payload is serialized event JSON. |
| `POST /me/notifications/{id}/read` | Recipient | No body. Records read timestamp, not consent. |
| `GET /appointments/{id}/notification-deliveries` | Staff | Up to 100 recent delivery records. |
| `POST /notification-deliveries/{id}/retry` | Staff | No body. Failed deliveries only; dispatcher rechecks relevance. |

Errors use Problem Details. Typical codes: `INVALID_TIME`, `INVALID_REFERENCE`, `PAYMENT_OPTION_UNAVAILABLE`, `QUOTE_EXPIRED`, `NO_AVAILABILITY`, `BOOKING_CONTENTION`, `STALE_REVISION`, `APPOINTMENT_STATE_CONFLICT`, `START_OUTSIDE_RESERVATION`, and `RESOURCE_OCCUPIED_OR_AT_RISK`. Missing demo identity is 401, staff-only denial is 403, and foreign-owned records are 404. Infrastructure failures are 503 with a trace ID; unexpected failures are 500. No database details are returned.

Start/cancel/completion replay of an already reached state returns that current state without duplicating events. Progress requires the current execution revision because it changes a forecast. A reason is required and limited to 500 characters.

The executable demo generates a future timestamp automatically. Overrun tests use an injected clock so they can exercise an operational scenario immediately. Real API callers cannot set the server clock or backdate actual work.
