using Scheduler.Application;
using Scheduler.Domain;

namespace Scheduler.Infrastructure;

public sealed class PostgresNotificationStore(Database database) : INotificationStore
{
    public async Task Materialize(DateTime now, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        await s.Begin();
        var events = await s.Many<Guid>("SELECT id FROM outbox_message WHERE processed_at IS NULL ORDER BY created_at,id LIMIT 50 FOR UPDATE SKIP LOCKED");
        if (events.Count != 0)
        {
            await s.Exec("""
                INSERT INTO notification_delivery(id,event_id,recipient_id,status,next_attempt_at)
                SELECT gen_random_uuid(),e.id,recipient,'Pending',@now
                FROM outbox_message e CROSS JOIN LATERAL unnest(e.recipients) recipient WHERE e.id=ANY(@ids)
                ON CONFLICT(event_id,recipient_id) DO NOTHING
                """, new { now, ids = events.ToArray() });
            await s.Exec("UPDATE outbox_message SET processed_at=@now WHERE id=ANY(@ids)", new { now, ids = events.ToArray() });
        }
        await s.Commit();
    }
    public async Task<DeliveryClaim?> Claim(DateTime now, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        await s.Begin();
        var delivery = await s.One<NotificationDispatcher.Delivery>("""
            SELECT * FROM notification_delivery WHERE
            (status='Pending' AND next_attempt_at<=@now) OR (status='Processing' AND lease_until<=@now)
            ORDER BY next_attempt_at,id LIMIT 1 FOR UPDATE SKIP LOCKED
            """, new { now });
        if (delivery == null) { await s.Commit(); return null; }
        var context = (await s.One<EventContext>("""
            SELECT (a.status='Cancelled' AND o.event_type='AppointmentConfirmed') obsolete, o.trace_parent, o.trace_state
            FROM outbox_message o JOIN appointment a ON a.id=o.appointment_id WHERE o.id=@id
            """, new { id = delivery.EventId }))!;
        var token = Guid.NewGuid();
        if (context.Obsolete)
            await s.Exec("UPDATE notification_delivery SET status='Suppressed',lease_token=NULL,lease_until=NULL WHERE id=@id", new { delivery.Id });
        else
            await s.Exec("UPDATE notification_delivery SET status='Processing',attempts=attempts+1,lease_token=@token,lease_until=@until WHERE id=@id",
                new { delivery.Id, token, until = now.AddSeconds(30) });
        await s.Commit();
        return new(delivery.Id, token, delivery.Attempts, context.Obsolete, context.TraceParent, context.TraceState);
    }
    public async Task Acknowledge(DeliveryClaim claim, string status, string? error, DateTime next, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        await s.Exec("""
            UPDATE notification_delivery SET status=@status,last_error=@error,next_attempt_at=@next,lease_token=NULL,lease_until=NULL
            WHERE id=@id AND lease_token=@token
            """, new { claim.Id, claim.Token, status, error, next });
    }
    public async Task<List<NotificationDispatcher.InboxItem>> Inbox(Guid recipient, int limit, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        return await s.Many<NotificationDispatcher.InboxItem>("""
            SELECT d.id,o.appointment_id,o.event_type,o.payload::text payload,o.created_at,d.status,d.read_at
            FROM notification_delivery d JOIN outbox_message o ON o.id=d.event_id
            WHERE d.recipient_id=@recipient AND d.status<>'Suppressed' ORDER BY o.created_at DESC,d.id LIMIT @limit
            """, new { recipient, limit });
    }
    public async Task<bool> Read(Guid id, Guid recipient, DateTime now, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        return await s.Exec("UPDATE notification_delivery SET read_at=COALESCE(read_at,@now) WHERE id=@id AND recipient_id=@recipient", new { id, recipient, now }) != 0;
    }
    public async Task<List<NotificationDispatcher.Delivery>> Deliveries(Guid appointmentId, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        return await s.Many<NotificationDispatcher.Delivery>("""
            SELECT d.* FROM notification_delivery d JOIN outbox_message o ON o.id=d.event_id
            WHERE o.appointment_id=@appointmentId ORDER BY o.created_at DESC LIMIT 100
            """, new { appointmentId });
    }
    public async Task<AppointmentSnapshot?> OwnerOfDelivery(Guid deliveryId, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        return await s.One<AppointmentSnapshot>("""
            SELECT a.* FROM appointment a JOIN outbox_message o ON o.appointment_id=a.id
            JOIN notification_delivery d ON d.event_id=o.id WHERE d.id=@deliveryId
            """, new { deliveryId });
    }
    public async Task<bool> Retry(Guid id, DateTime now, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        return await s.Exec("UPDATE notification_delivery SET status='Pending',attempts=0,next_attempt_at=@now WHERE id=@id AND status='Failed'", new { id, now }) != 0;
    }
    private sealed class EventContext
    {
        public bool Obsolete { get; set; }
        public string? TraceParent { get; set; }
        public string? TraceState { get; set; }
    }
}
