namespace Scheduler.Application;

public sealed class NotificationDispatcher(INotificationStore notifications, ISchedulingStore scheduling, TimeProvider clock, IDeliveryProvider provider)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public async Task<int> DispatchBatch(CancellationToken ct)
    {
        await notifications.Materialize(Now, ct);
        var count = 0;
        for (; count < 50; count++)
        {
            var claim = await notifications.Claim(Now, ct);
            if (claim == null) break;
            if (claim.Suppressed) continue;
            var success = false;
            // Claim is committed before the provider is invoked.
            try { await provider.Send(claim.Id, ct); success = true; }
            catch (DeliveryUnavailableException) { }
            var status = success ? "ProviderAccepted" : claim.PreviousAttempts + 1 >= 5 ? "Failed" : "Pending";
            await notifications.Acknowledge(claim, status, success ? null : "PROVIDER_UNAVAILABLE",
                Now.AddSeconds(Math.Min(60, Math.Pow(2, claim.PreviousAttempts + 1)) + Random.Shared.NextDouble()), ct);
            Telemetry.Deliveries.Add(1, new KeyValuePair<string, object?>("outcome", status));
        }
        return count;
    }
    public Task<List<InboxItem>> Inbox(Actor actor, int limit, CancellationToken ct) => notifications.Inbox(actor.Id, Math.Clamp(limit, 1, 100), ct);
    public async Task Read(Guid id, Actor actor, CancellationToken ct)
    {
        if (!await notifications.Read(id, actor.Id, Now, ct)) throw new RequestFailure(FailureType.NotFound, "NOT_FOUND", "Notification not found.");
    }
    public async Task<List<Delivery>> Deliveries(Guid id, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await scheduling.Open(ct);
        var appointment = await AppointmentWorkflow.Find(s, id);
        actor.Owns(appointment.Snapshot.CustomerId, appointment.Snapshot.DealershipId);
        return await notifications.Deliveries(id, ct);
    }
    public async Task Retry(Guid id, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        var appointment = await notifications.OwnerOfDelivery(id, ct)
            ?? throw new RequestFailure(FailureType.NotFound, "NOT_FOUND", "Notification not found.");
        actor.Owns(appointment.CustomerId, appointment.DealershipId);
        if (!await notifications.Retry(id, Now, ct))
            throw new RequestFailure(FailureType.Conflict, "DELIVERY_STATE_CONFLICT", "Only failed deliveries can be retried.");
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
