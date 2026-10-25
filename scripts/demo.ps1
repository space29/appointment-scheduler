param([string]$BaseUrl = 'http://127.0.0.1:5080')
$ErrorActionPreference = 'Stop'
$headers = @{ 'X-Demo-Actor' = 'customer-a' }
$reference = Invoke-RestMethod "$BaseUrl/reference-data" -Headers $headers
$localNow = [DateTimeOffset]::UtcNow.ToOffset([TimeSpan]::FromHours(7))
$start = [DateTimeOffset]::new($localNow.Date.AddDays(1).AddHours(10), [TimeSpan]::FromHours(7))
$request = @{
    customerId = $reference.customers[0].id
    vehicleId = $reference.vehicles[0].id
    dealershipId = $reference.dealerships[0].id
    serviceTypeId = $reference.services[0].id
    startsAt = $start.ToString('o')
}
$quote = Invoke-RestMethod "$BaseUrl/appointment-quotes" -Method Post -Headers $headers -ContentType 'application/json' -Body ($request | ConvertTo-Json)
Write-Output "Quote: $($quote.total) $($quote.currency), $($quote.estimatedMinutes) work minutes + $($quote.bufferMinutes) buffer"
$bookingBody = @{ quoteId = $quote.id } | ConvertTo-Json
$booking = Invoke-RestMethod "$BaseUrl/appointments" -Method Post -Headers $headers -ContentType 'application/json' -Body $bookingBody
$replay = Invoke-RestMethod "$BaseUrl/appointments" -Method Post -Headers $headers -ContentType 'application/json' -Body $bookingBody
if ($booking.appointment.id -ne $replay.appointment.id) { throw 'Idempotency check failed.' }
Write-Output "Confirmed: $($booking.appointment.id), $($booking.appointment.paymentTiming). Same-quote retry returned the same appointment."
$cancel = @{ expectedRevision = $booking.appointment.revision; reason = 'Executable demonstration cleanup' } | ConvertTo-Json
$cancelled = Invoke-RestMethod "$BaseUrl/appointments/$($booking.appointment.id)/cancel" -Method Post -Headers $headers -ContentType 'application/json' -Body $cancel
Write-Output "Cancellation: $($cancelled.appointment.status); reservations retained but released."
Start-Sleep -Milliseconds 1200
$inbox = Invoke-RestMethod "$BaseUrl/me/notifications" -Headers $headers
$inbox | Select-Object eventType,status,appointmentId | Format-Table
