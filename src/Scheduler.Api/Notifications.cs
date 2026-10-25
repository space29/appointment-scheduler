namespace Scheduler.Api;

public interface IDeliveryProvider
{
    Task Send(Guid deliveryId, CancellationToken ct);
}

// Deliberately never contacts customers. Stable delivery identity is passed to future provider adapters.
public sealed class MockDeliveryProvider(IConfiguration config, ILogger<MockDeliveryProvider> logger) : IDeliveryProvider
{
    public Task Send(Guid deliveryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (config.GetValue<bool>("Notifications:SimulateFailure")) throw new HttpRequestException("Simulated provider outage");
        logger.LogInformation("Mock provider accepted delivery {DeliveryId}", deliveryId);
        return Task.CompletedTask;
    }
}

public sealed class NotificationDispatcher(Database database, TimeProvider clock, IDeliveryProvider provider)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<int> DispatchBatch(CancellationToken ct)
    {
        await using (var s = await database.Open(ct))
        {
            await s.Begin();
            var events = await s.Many<Guid>("SELECT id FROM outbox_message WHERE processed_at IS NULL ORDER BY created_at,id LIMIT 50 FOR UPDATE SKIP LOCKED");
            if (events.Count > 0)
            {
                await s.Exec("""
                    INSERT INTO notification_delivery(id,event_id,recipient_id,status,next_attempt_at)
                    SELECT gen_random_uuid(),e.id,recipient,'Pending',@now
                    FROM outbox_message e CROSS JOIN LATERAL unnest(e.recipients) recipient WHERE e.id=ANY(@ids)
                    ON CONFLICT(event_id,recipient_id) DO NOTHING
                    """, new { now = Now, ids = events.ToArray() });
                await s.Exec("UPDATE outbox_message SET processed_at=@now WHERE id=ANY(@ids)", new { now = Now, ids = events.ToArray() });
            }
            await s.Commit();
        }
        var count = 0;
        for (; count < 50; count++)
        {
            Delivery? delivery;
            var token = Guid.NewGuid();
            await using (var s = await database.Open(ct))
            {
                await s.Begin();
                delivery = await s.One<Delivery>("""
                    SELECT * FROM notification_delivery WHERE
                    (status='Pending' AND next_attempt_at<=@now) OR (status='Processing' AND lease_until<=@now)
                    ORDER BY next_attempt_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
                    """, new { now = Now });
                if (delivery == null) { await s.Commit(); break; }
                var obsolete = await s.Scalar<bool>("""
                    SELECT a.status='Cancelled' AND o.event_type='AppointmentConfirmed'
                    FROM outbox_message o JOIN appointment a ON a.id=o.appointment_id WHERE o.id=@id
                    """, new { id = delivery.EventId });
                if (obsolete)
                {
                    await s.Exec("UPDATE notification_delivery SET status='Suppressed',lease_token=NULL,lease_until=NULL WHERE id=@id", new { delivery.Id });
                    await s.Commit();
                    continue;
                }
                await s.Exec("""
                    UPDATE notification_delivery SET status='Processing',attempts=attempts+1,lease_token=@token,lease_until=@until WHERE id=@id
                    """, new { delivery.Id, token, until = Now.AddSeconds(30) });
                await s.Commit();
            }
            // Provider call is outside every database transaction.
            var success = false;
            try { await provider.Send(delivery.Id, ct); success = true; }
            catch (HttpRequestException) { /* A bounded retry is persisted below. */ }
            var status = success ? "ProviderAccepted" : delivery.Attempts + 1 >= 5 ? "Failed" : "Pending";
            await using (var s = await database.Open(ct))
            {
                await s.Exec("""
                    UPDATE notification_delivery SET status=@status,last_error=@error,next_attempt_at=@next,lease_token=NULL,lease_until=NULL
                    WHERE id=@id AND lease_token=@token
                    """, new { delivery.Id, token, status, error = success ? null : "PROVIDER_UNAVAILABLE", next = Now.AddSeconds(Math.Min(60, Math.Pow(2, delivery.Attempts + 1)) + Random.Shared.NextDouble()) });
            }
            Telemetry.Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", status));
        }
        return count;
    }

    public async Task<object> Inbox(Actor actor, int limit, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        return await s.Many<InboxItem>("""
            SELECT d.id,o.appointment_id,o.event_type,o.payload::text payload,o.created_at,d.status,d.read_at
            FROM notification_delivery d JOIN outbox_message o ON o.id=d.event_id
            WHERE d.recipient_id=@id AND d.status<>'Suppressed' ORDER BY o.created_at DESC,d.id LIMIT @limit
            """, new { actor.Id, limit = Math.Clamp(limit, 1, 100) });
    }

    public async Task Read(Guid id, Actor actor, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        if (await s.Exec("UPDATE notification_delivery SET read_at=COALESCE(read_at,@now) WHERE id=@id AND recipient_id=@recipient", new { id, recipient = actor.Id, now = Now }) == 0)
            throw new DomainError(404, "NOT_FOUND", "Notification not found.");
    }

    public async Task<object> Deliveries(Guid id, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await database.Open(ct);
        var a = await BookingService.Find(s, id);
        actor.Owns(a.CustomerId, a.DealershipId);
        return await s.Many<Delivery>("SELECT d.* FROM notification_delivery d JOIN outbox_message o ON o.id=d.event_id WHERE o.appointment_id=@id ORDER BY o.created_at DESC LIMIT 100", new { id });
    }

    public async Task Retry(Guid id, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await database.Open(ct);
        var a = await s.One<Appointment>("""
            SELECT a.* FROM appointment a JOIN outbox_message o ON o.appointment_id=a.id
            JOIN notification_delivery d ON d.event_id=o.id WHERE d.id=@id
            """, new { id }) ?? throw new DomainError(404, "NOT_FOUND", "Notification not found.");
        actor.Owns(a.CustomerId, a.DealershipId);
        if (await s.Exec("UPDATE notification_delivery SET status='Pending',attempts=0,next_attempt_at=@now WHERE id=@id AND status='Failed'", new { id, now = Now }) == 0)
            throw new DomainError(409, "DELIVERY_STATE_CONFLICT", "Only failed deliveries can be retried.");
        // Dispatcher checks relevance again immediately before sending.
    }

    public sealed class Delivery
    {
        public Guid Id { get; set; }
        public Guid EventId { get; set; }
        public Guid RecipientId { get; set; }
        public string Status { get; set; } = "";
        public int Attempts { get; set; }
        public DateTime NextAttemptAt { get; set; }
        public string? LastError { get; set; }
    }
    public sealed record InboxItem(Guid Id, Guid AppointmentId, string EventType, string Payload, DateTime CreatedAt, string Status, DateTime? ReadAt);
}

public sealed class NotificationWorker(NotificationDispatcher dispatcher, IConfiguration config, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notifications:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await dispatcher.DispatchBatch(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Notification worker failed with {ErrorType}; durable work will be retried", ex.GetType().Name); }
            try { await Task.Delay(Math.Clamp(config.GetValue("Notifications:PollMilliseconds", 500), 50, 10000), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
