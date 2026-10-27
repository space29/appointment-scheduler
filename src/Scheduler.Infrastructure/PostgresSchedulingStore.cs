using System.Text.Json;
using Scheduler.Application;
using Scheduler.Domain;

namespace Scheduler.Infrastructure;

public sealed class PostgresSchedulingStore(Database database, ReferenceDataCache cache) : ISchedulingStore
{
    public async Task<ISchedulingSession> Open(CancellationToken ct) => new PostgresSchedulingSession(await database.Open(ct), cache, ct);
}

internal sealed class PostgresSchedulingSession(Session s, ReferenceDataCache cache, CancellationToken ct) : ISchedulingSession
{
    private QuotePolicy? quotedPolicy;
    public Task Begin() => s.Begin();
    public Task Commit() => s.Commit();
    public ValueTask DisposeAsync() => s.DisposeAsync();
    public Task LockResources(Guid[] ids) => s.LockResources(ids);

    public async Task<QuoteInputs?> QuoteInputs(QuoteRequest request)
    {
        // Never let a warm policy cache replace current vehicle ownership/category validation.
        var category = await s.One<string>("SELECT category FROM vehicle WHERE id=@VehicleId AND customer_id=@CustomerId", request);
        if (category == null) return null;
        quotedPolicy = await cache.Get<QuotePolicy>(ReferenceCacheRegion.QuotePolicy,
            (request.DealershipId, request.ServiceTypeId), async () =>
            {
                var row = await s.One<QuotePolicy.Row>("""
        SELECT p.id policy_id,p.base_price,p.currency,p.version price_version,d.time_zone,st.baseline_minutes,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('Category',r.category,'EstimatedMinutes',r.estimated_minutes,
            'BufferMinutes',r.buffer_minutes,'Version',r.version)) FROM duration_rule r WHERE r.service_type_id=st.id),'[]'::jsonb)::text duration_rules,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('Name',b.name,'Weekday',b.weekday,'StartMinute',b.start_minute,
            'EndMinute',b.end_minute,'Multiplier',b.multiplier)) FROM price_band b WHERE b.policy_id=p.id),'[]'::jsonb)::text price_bands
        FROM price_policy p JOIN service_type st ON st.id=p.service_type_id
        JOIN dealership d ON d.id=@DealershipId
        WHERE p.dealership_id=d.id AND p.service_type_id=@ServiceTypeId
        """, request);
                return row?.Snapshot();
            }, ct);
        if (quotedPolicy == null) return null;
        var duration = quotedPolicy.Durations.SingleOrDefault(r => r.Category == category);
        return new QuoteInputs
        {
            EstimatedMinutes = duration?.EstimatedMinutes ?? quotedPolicy.BaselineMinutes,
            BufferMinutes = duration?.BufferMinutes ?? 0,
            DurationVersion = duration?.Version ?? 0,
            PolicyId = quotedPolicy.Id, BasePrice = quotedPolicy.BasePrice, Currency = quotedPolicy.Currency,
            PriceVersion = quotedPolicy.Version, TimeZone = quotedPolicy.TimeZone
        };
    }
    public Task<PriceBand?> PriceBand(Guid policyId, int weekday, int minute)
    {
        // Use the same snapshot throughout quote issuance, even if the cache expires between calls.
        if (quotedPolicy?.Id == policyId)
        {
            var band = quotedPolicy.Bands.SingleOrDefault(b => b.Weekday == weekday && b.StartMinute <= minute && b.EndMinute > minute);
            return Task.FromResult(band == null ? null : new PriceBand(band.Name, band.Multiplier));
        }
        return s.One<PriceBand>("""
        SELECT name,multiplier FROM price_band WHERE policy_id=@policyId AND weekday=@weekday
        AND start_minute<=@minute AND end_minute>@minute
        """, new { policyId, weekday, minute });
    }
    public async Task AddQuote(QuoteSnapshot q) => await s.Exec("""
        INSERT INTO appointment_quote(id,customer_id,vehicle_id,dealership_id,service_type_id,starts_at,ends_at,
        estimated_minutes,buffer_minutes,duration_version,base_price,multiplier,band,total,currency,price_version,created_at,expires_at)
        VALUES(@Id,@CustomerId,@VehicleId,@DealershipId,@ServiceTypeId,@StartsAt,@EndsAt,
        @EstimatedMinutes,@BufferMinutes,@DurationVersion,@BasePrice,@Multiplier,@Band,@Total,@Currency,@PriceVersion,@CreatedAt,@ExpiresAt)
        """, q);
    public Task<QuoteSnapshot?> Quote(Guid id, bool forUpdate = false) => s.One<QuoteSnapshot>(
        "SELECT * FROM appointment_quote WHERE id=@id" + (forUpdate ? " FOR UPDATE" : ""), new { id });
    public async Task<ServiceAppointment?> ByQuote(Guid quoteId) => await Restore(
        await s.One<AppointmentSnapshot>("SELECT * FROM appointment WHERE quote_id=@quoteId", new { quoteId }), false);
    public async Task<ServiceAppointment?> Appointment(Guid id, bool forUpdate = false) => await Restore(
        await s.One<AppointmentSnapshot>("SELECT * FROM appointment WHERE id=@id" + (forUpdate ? " FOR UPDATE" : ""), new { id }), forUpdate);
    private async Task<ServiceAppointment?> Restore(AppointmentSnapshot? a, bool locked)
    {
        if (a == null) return null;
        var execution = await s.One<ExecutionSnapshot>("SELECT * FROM job_execution WHERE appointment_id=@id" + (locked ? " FOR UPDATE" : ""), new { a.Id });
        return ServiceAppointment.Restore(a, execution);
    }
    public async Task<Guid[]> ResourceIds(Guid appointmentId) =>
        (await s.Many<Guid>("SELECT resource_id FROM resource_reservation WHERE appointment_id=@appointmentId", new { appointmentId })).ToArray();

    public Task<List<Guid>> Candidates(QuoteSnapshot q, string kind, DateTime now) => s.Many<Guid>("""
        SELECT r.id FROM resource r WHERE r.dealership_id=@DealershipId AND r.kind=@kind
        AND (@kind='Bay' OR EXISTS(SELECT 1 FROM qualification k WHERE k.resource_id=r.id AND k.service_type_id=@ServiceTypeId))
        AND EXISTS(SELECT 1 FROM availability w WHERE w.resource_id=r.id AND w.starts_at<=@StartsAt AND w.ends_at>=@EndsAt)
        AND NOT EXISTS(SELECT 1 FROM resource_reservation b WHERE b.resource_id=r.id AND b.is_active
          AND tstzrange(b.starts_at,b.ends_at,'[)') && tstzrange(@StartsAt,@EndsAt,'[)'))
        AND NOT EXISTS(SELECT 1 FROM resource_usage u JOIN job_execution e ON e.id=u.execution_id
          WHERE u.resource_id=r.id AND u.released_at IS NULL
          AND (e.expected_completion<=@now OR (e.actual_start<@EndsAt AND e.expected_completion>@StartsAt)))
        ORDER BY r.id LIMIT 20
        """, new { q.DealershipId, q.ServiceTypeId, q.StartsAt, q.EndsAt, kind, now });

    public async Task AddBooking(ServiceAppointment appointment, Guid bayId, Guid technicianId)
    {
        var a = appointment.Snapshot;
        await s.Exec("""
            INSERT INTO appointment(id,quote_id,customer_id,vehicle_id,dealership_id,service_type_id,starts_at,ends_at,
            original_starts_at,original_ends_at,estimated_minutes,buffer_minutes,duration_version,price_version,total,currency,
            payment_timing,booking_channel,status,revision,created_at)
            VALUES(@Id,@QuoteId,@CustomerId,@VehicleId,@DealershipId,@ServiceTypeId,@StartsAt,@EndsAt,
            @OriginalStartsAt,@OriginalEndsAt,@EstimatedMinutes,@BufferMinutes,@DurationVersion,@PriceVersion,@Total,@Currency,
            @PaymentTiming,@BookingChannel,@Status,@Revision,@CreatedAt)
            """, a);
        foreach (var (resource, role) in new[] { (bayId, "Bay"), (technicianId, "LeadTechnician") })
            await s.Exec("""
                INSERT INTO resource_reservation(id,appointment_id,resource_id,role,starts_at,ends_at)
                VALUES(@id,@appointmentId,@resource,@role,@StartsAt,@EndsAt)
                """, new { id = Guid.NewGuid(), appointmentId = a.Id, resource, role, a.StartsAt, a.EndsAt });
    }

    public async Task Save(ServiceAppointment appointment)
    {
        await s.Exec("""
            UPDATE appointment SET status=@Status,revision=@Revision,cancelled_at=@CancelledAt,
            cancelled_by=@CancelledBy,cancellation_reason=@CancellationReason WHERE id=@Id
            """, appointment.Snapshot);
        if (appointment.Execution is { } execution)
            await s.Exec("""
                INSERT INTO job_execution(id,appointment_id,actual_start,expected_completion,actual_completion,revision)
                VALUES(@Id,@AppointmentId,@ActualStart,@ExpectedCompletion,@ActualCompletion,@Revision)
                ON CONFLICT(appointment_id) DO UPDATE SET expected_completion=EXCLUDED.expected_completion,
                actual_completion=EXCLUDED.actual_completion,revision=EXCLUDED.revision
                """, execution.Snapshot);
    }
    public async Task<AppointmentView> View(ServiceAppointment appointment) => new(appointment.Snapshot.ToDto(),
        await s.Many<Reservation>("SELECT resource_id,role,starts_at,ends_at,is_active FROM resource_reservation WHERE appointment_id=@id ORDER BY role", new { appointment.Snapshot.Id }),
        appointment.Execution?.Snapshot.ToDto(),
        await s.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM disruption_incident WHERE affected_appointment_id=@id AND status='Open')", new { appointment.Snapshot.Id }));
    public async Task ReleaseReservations(Guid appointmentId, DateTime now) => await s.Exec(
        "UPDATE resource_reservation SET is_active=false,released_at=@now,release_reason='Cancellation' WHERE appointment_id=@appointmentId AND is_active", new { appointmentId, now });
    public async Task ResolveCancelledImpacts(Guid appointmentId, DateTime now) => await s.Exec(
        "UPDATE disruption_incident SET status='Resolved',updated_at=@now WHERE affected_appointment_id=@appointmentId AND status='Open'", new { appointmentId, now });
    public Task<bool> ResourcesBlocked(Guid appointmentId) => s.Scalar<bool>("""
        SELECT EXISTS(SELECT 1 FROM resource_usage u JOIN resource_reservation r ON r.resource_id=u.resource_id
        WHERE r.appointment_id=@appointmentId AND r.is_active AND u.released_at IS NULL)
        OR EXISTS(SELECT 1 FROM disruption_incident WHERE affected_appointment_id=@appointmentId AND status='Open')
        """, new { appointmentId });
    public async Task AcquireResources(ExecutionSnapshot e) => await s.Exec("""
        INSERT INTO resource_usage(id,execution_id,resource_id,acquired_at)
        SELECT gen_random_uuid(),@Id,resource_id,@ActualStart FROM resource_reservation WHERE appointment_id=@AppointmentId AND is_active
        """, e);
    public async Task ReleaseUsage(Guid executionId, DateTime now) => await s.Exec(
        "UPDATE resource_usage SET released_at=@now WHERE execution_id=@executionId AND released_at IS NULL", new { executionId, now });
    public async Task AddExecutionReport(ExecutionSnapshot e, Guid actor, string reason, DateTime now) => await s.Exec(
        "INSERT INTO execution_report VALUES(@report,@Id,@actor,@ExpectedCompletion,@reason,@now)", new { report = Guid.NewGuid(), e.Id, actor, e.ExpectedCompletion, reason, now });
    public Task<List<AppointmentSnapshot>> Affected(ServiceAppointment a, DateTime end) => s.Many<AppointmentSnapshot>("""
        SELECT DISTINCT b.* FROM appointment b JOIN resource_reservation r ON r.appointment_id=b.id AND r.is_active
        JOIN resource_usage u ON u.resource_id=r.resource_id AND u.execution_id=@executionId
        WHERE b.id<>@id AND b.status='Confirmed' AND b.starts_at<@end AND b.ends_at>@actualStart
        """, new { a.Snapshot.Id, executionId = a.Execution!.Snapshot.Id, end, actualStart = a.Execution.Snapshot.ActualStart });
    public async Task ReconcileImpacts(Guid executionId, IReadOnlyList<ImpactDecision> decisions, DateTime now)
    {
        await s.Exec("UPDATE disruption_incident SET status='Resolved',updated_at=@now WHERE execution_id=@executionId AND status='Open'", new { executionId, now });
        foreach (var d in decisions)
            await s.Exec("""
                INSERT INTO disruption_incident(id,execution_id,affected_appointment_id,reason,status,updated_at)
                VALUES(@id,@executionId,@AppointmentId,@Reason,@status,@now)
                ON CONFLICT(execution_id,affected_appointment_id) DO UPDATE SET reason=EXCLUDED.reason,status=EXCLUDED.status,updated_at=EXCLUDED.updated_at
                """, new { id = Guid.NewGuid(), executionId, d.AppointmentId, d.Reason, status = d.IsOpen ? "Open" : "Resolved", now });
    }
    public Task<bool> OutsideShift(ExecutionSnapshot e, DateTime end) => s.Scalar<bool>("""
        SELECT EXISTS(SELECT 1 FROM resource_usage u WHERE u.execution_id=@Id
        AND NOT EXISTS(SELECT 1 FROM availability w WHERE w.resource_id=u.resource_id AND w.starts_at<=@ActualStart AND w.ends_at>=@end))
        """, new { e.Id, e.ActualStart, end });
    public Task<List<Incident>> Impacts(Guid appointmentId) => s.Many<Incident>("""
        SELECT i.* FROM disruption_incident i JOIN job_execution e ON e.id=i.execution_id
        WHERE e.appointment_id=@appointmentId OR i.affected_appointment_id=@appointmentId ORDER BY i.updated_at DESC
        """, new { appointmentId });
    public async Task AppendEvents(IReadOnlyList<OutboxEvent> events)
    {
        foreach (var entry in events)
        {
            var e = entry.Event;
            var a = e.Appointment;
            object? details = e switch
            {
                ProgressReported p => new { expectedCompletion = p.ExpectedCompletion, p.Reason },
                WorkCompleted c => new { actualCompletion = c.ActualCompletion, c.Reason },
                OperationalImpact i => new { affectedAppointments = i.AffectedAppointments, outsideShift = i.OutsideShift, completed = i.Completed, expectedOrActualCompletion = i.ExpectedOrActualCompletion },
                _ => null
            };
            var payload = JsonSerializer.Serialize(new { appointmentId = a.Id, a.StartsAt, a.EndsAt, a.Total, a.Currency, a.PaymentTiming, a.Status, details });
            await s.Exec("""
                INSERT INTO outbox_message(id,appointment_id,event_type,revision,recipients,payload,created_at,trace_parent,trace_state)
                VALUES(@id,@appointmentId,@type,@revision,@recipients,CAST(@payload AS jsonb),@now,@traceParent,@traceState)
                ON CONFLICT(appointment_id,event_type,revision) DO NOTHING
                """, new { id = Guid.NewGuid(), appointmentId = a.Id, type = e.GetType().Name, revision = a.Revision, recipients = entry.Recipients, payload, now = e.OccurredAt,
                    traceParent = System.Diagnostics.Activity.Current?.Id, traceState = System.Diagnostics.Activity.Current?.TraceStateString });
        }
    }
}
