Local API playground

The API is available at `http://127.0.0.1:5080`. You can use the browser at **http://127.0.0.1:5080/swagger**: click **Authorize**, enter `customer-a`, then use **Try it out** and **Execute** on each endpoint. For a new request, copy the returned quote or appointment ID into the next request. Change authorization to `staff` for delivery inspection and workshop operations.

Alternatively, run the following blocks one at a time in the **same PowerShell terminal**. They are inline commands, so they do not require permission to execute a `.ps1` file. These requests create a demo booking and later cancel it; the history remains in PostgreSQL.

**1. Set up the customer and check readiness**

```powershell
$baseUrl = 'http://127.0.0.1:5080'
$demoHeaders = @{ 'X-Demo-Actor' = 'customer-a' }

Invoke-RestMethod "$baseUrl/health/ready"
Invoke-RestMethod "$baseUrl/reference-data" -Headers $demoHeaders |
    ConvertTo-Json -Depth 6
```

Expected: `Ready`, customer A's vehicle, Inspection service, Web booking channel, and PayAtCheckIn. The demo header acts as the local identity. No database credentials are needed for these requests.

**2. Request a quote**

This example requests **27 October 2026 at 10:00 AM Vietnam time**. If you return to this guide later, change the date to a future day within seeded availability. Confirm within ten minutes, or create a fresh quote.

```powershell
$quoteBody = @{
    customerId = '10000000-0000-0000-0000-000000000001'
    vehicleId = '20000000-0000-0000-0000-000000000001'
    dealershipId = '30000000-0000-0000-0000-000000000001'
    serviceTypeId = '40000000-0000-0000-0000-000000000001'
    startsAt = '2026-10-27T10:00:00+07:00'
} | ConvertTo-Json

$quote = Invoke-RestMethod "$baseUrl/appointment-quotes" `
    -Method Post -Headers $demoHeaders -ContentType 'application/json' -Body $quoteBody

$quote | ConvertTo-Json -Depth 6
```

Inspect `id`, `estimatedMinutes`, `bufferMinutes`, `startsAt`, `endsAt`, `total`, `currency`, `band`, `expiresAt`, and `paymentOptions`. With the seeded Standard vehicle, the estimate is 60 minutes plus a 10-minute buffer. UTC response timestamps may display 03:00 rather than 10:00; they represent the same instant. A quote stores terms but does not hold resources or guarantee availability.

Video narration: “The quote captures the estimated duration, buffer, and price before the customer confirms.”

**3. Confirm the booking**

```powershell
$bookingBody = @{
    quoteId = $quote.id
    paymentTiming = 'PayAtCheckIn'
} | ConvertTo-Json

$createdResponse = Invoke-WebRequest "$baseUrl/appointments" `
    -Method Post -Headers $demoHeaders -ContentType 'application/json' `
    -Body $bookingBody -UseBasicParsing

$booking = $createdResponse.Content | ConvertFrom-Json
$appointmentId = $booking.appointment.id
$traceId = $createdResponse.Headers['X-Trace-Id']

$createdResponse.StatusCode
$traceId
$booking | ConvertTo-Json -Depth 8
```

Expected: **201 Created**, `appointment.status = Confirmed`, and two resource assignments: a Bay and a Technician. Both cover the appointment interval. Keep `$appointmentId` and `$traceId` for the next steps. Payment timing is an arrangement, not a payment receipt. If confirmation returns `NO_AVAILABILITY`, try another free future start and request a fresh quote.

Video narration: “The appointment is confirmed only after the booking and resource reservations commit.”

**4. Retry exactly the same confirmation**

```powershell
$retryResponse = Invoke-WebRequest "$baseUrl/appointments" `
    -Method Post -Headers $demoHeaders -ContentType 'application/json' `
    -Body $bookingBody -UseBasicParsing

$replayedBooking = $retryResponse.Content | ConvertFrom-Json
$retryResponse.StatusCode
$replayedBooking.appointment.id -eq $appointmentId
```

Expected: **200 OK** and `True`. No second appointment is created. Use the same quote ID; a fresh quote is a new booking attempt.

Video narration: “A retry returns the existing appointment, so a lost response does not create a duplicate booking.”

**5. Read the persisted appointment and its confirmation notification**

```powershell
Invoke-RestMethod "$baseUrl/appointments/$appointmentId" -Headers $demoHeaders |
    ConvertTo-Json -Depth 8

$inbox = Invoke-RestMethod "$baseUrl/me/notifications?limit=100" -Headers $demoHeaders
$inbox | Where-Object { $_.appointmentId -eq $appointmentId } |
    ConvertTo-Json -Depth 6
```

The worker runs asynchronously. If the inbox entry is not visible yet, repeat the inbox request after a second. `ProviderAccepted` means the mock provider accepted the notification; it does not mean an actual email/SMS was delivered or read. There are no real customer messages.

**6. Cancel before work starts**

```powershell
$current = Invoke-RestMethod "$baseUrl/appointments/$appointmentId" -Headers $demoHeaders
$cancelBody = @{
    expectedRevision = $current.appointment.revision
    reason = 'Customer changed plans during API demonstration'
} | ConvertTo-Json

$cancelled = Invoke-RestMethod "$baseUrl/appointments/$appointmentId/cancel" `
    -Method Post -Headers $demoHeaders -ContentType 'application/json' -Body $cancelBody

$cancelled | ConvertTo-Json -Depth 8
```

Expected: `appointment.status = Cancelled`, a higher appointment revision, and resource reservations retained with `isActive = false`. Replaying the original booking request after cancellation returns this cancelled appointment. To book again, start with a new quote.

Video narration: “Cancellation preserves history and releases the appointment's planned capacity.”

**7. Inspect cancellation notifications and delivery status**

```powershell
$inbox = Invoke-RestMethod "$baseUrl/me/notifications?limit=100" -Headers $demoHeaders
$inbox | Where-Object { $_.appointmentId -eq $appointmentId } |
    ConvertTo-Json -Depth 6

$staffHeaders = @{ 'X-Demo-Actor' = 'staff' }
Invoke-RestMethod "$baseUrl/appointments/$appointmentId/notification-deliveries" `
    -Headers $staffHeaders | ConvertTo-Json -Depth 6
```

Repeat after a second if processing is still pending. Expect cancellation notifications for the customer and staff. If cancellation happens before confirmation delivery, the unsent confirmation may be suppressed. That is expected behavior.

**8. Explore observability**

```powershell
# Show only booking and notification metrics from the full scrape.
$metrics = (Invoke-WebRequest "$baseUrl/metrics" -UseBasicParsing).Content
$metrics -split "`n" | Where-Object {
    $_ -match '^scheduler_(booking_outcomes|booking_duration|notification_outcomes)'
}

# Search the running API's JSON logs for your booking trace.
Get-Content 'C:\Projects\appointment-scheduler\artifacts\local-api.stdout.log' |
    Select-String -SimpleMatch $traceId
```

Metrics are cumulative for the running process; earlier experiments can contribute to their totals. The trace ID narrows the logs to the request and its related notification work. This startup leaves console trace printing disabled; use the JSON logs and metrics here. See `docs/observability.md` to enable a trace viewer.

**More cases to try**

| Experiment | Expected behavior |
| --- | --- |
| Repeat cancellation | Returns the current cancelled state without another cancellation event. |
| Read A's appointment using `X-Demo-Actor: customer-b` | 404; another customer's record is not disclosed. |
| Omit `X-Demo-Actor` from a business request | 401. |
| Confirm a fresh valid quote with `paymentTiming: PayOnline` | Rejected with `PAYMENT_OPTION_UNAVAILABLE`. |
| Request quotes at different configured pricing times | Inspect `band` and `total`; accepted booking prices remain fixed. |

Work start/progress/completion form a separate operational demonstration. This booking is tomorrow, so starting it now is expected to fail with `START_OUTSIDE_RESERVATION`. The API uses real server time; the automated overrun scenarios use a test clock.

Useful addresses: `/swagger` (interactive API explorer), `/health/ready`, `/openapi/v1.json` (the API schema), and `/metrics`. The background process identity is recorded in ignored `artifacts/local-api.json`; its logs are in `artifacts/local-api.stdout.log` and `artifacts/local-api.stderr.log`.
