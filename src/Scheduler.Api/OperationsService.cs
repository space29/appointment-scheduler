namespace Scheduler.Api;

public sealed class OperationsService(Database database, TimeProvider clock)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<AppointmentView> Start(Guid id, ChangeRequest request, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        Rules.Reason(request.Reason);
        await using var s = await database.Open(ct);
        await s.Begin();
        var a = await BookingService.LockAppointment(s, id, actor);
        if (a.Status == "InProgress") return await BookingService.View(s, a);
        Rules.Revision(request.ExpectedRevision, a.Revision);
        if (a.Status != "Confirmed") throw State();
        var now = Now;
        // The buffer can absorb a small late start. Never squeeze the estimated work to fit.
        var expected = now.AddMinutes(a.EstimatedMinutes);
        if (now < a.StartsAt || expected > a.EndsAt)
            throw new DomainError(409, "START_OUTSIDE_RESERVATION", "The full remaining work must fit the reservation. Staff recovery is required.");
        if (await s.Scalar<bool>("""
            SELECT EXISTS(SELECT 1 FROM resource_usage u JOIN resource_reservation r ON r.resource_id=u.resource_id
            WHERE r.appointment_id=@id AND r.is_active AND u.released_at IS NULL)
            OR EXISTS(SELECT 1 FROM disruption_incident WHERE affected_appointment_id=@id AND status='Open')
            """, new { id }))
            throw new DomainError(409, "RESOURCE_OCCUPIED_OR_AT_RISK", "An assigned resource is occupied or this appointment has an unresolved disruption.");
        var executionId = Guid.NewGuid();
        await s.Exec("INSERT INTO job_execution(id,appointment_id,actual_start,expected_completion) VALUES(@executionId,@id,@now,@expected)", new { executionId, id, now, expected });
        await s.Exec("""
            INSERT INTO resource_usage(id,execution_id,resource_id,acquired_at)
            SELECT gen_random_uuid(),@executionId,resource_id,@now FROM resource_reservation WHERE appointment_id=@id AND is_active
            """, new { executionId, id, now });
        a = (await s.One<Appointment>("UPDATE appointment SET status='InProgress',revision=revision+1 WHERE id=@id RETURNING *", new { id }))!;
        await s.Exec("INSERT INTO execution_report VALUES(@report,@executionId,@actor,@expected,@reason,@now)", new { report = Guid.NewGuid(), executionId, actor = actor.Id, expected, reason = request.Reason, now });
        var view = await BookingService.View(s, a);
        await s.Commit();
        return view;
    }

    public async Task<AppointmentView> Progress(Guid id, ProgressRequest request, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        Rules.Reason(request.Reason);
        var end = request.ExpectedCompletion.UtcDateTime;
        if (end <= Now || end > Now.AddDays(7))
            throw new DomainError(400, "INVALID_FORECAST", "Expected completion must be in the future and within seven days.");
        await using var s = await database.Open(ct);
        await s.Begin();
        var a = await BookingService.LockAppointment(s, id, actor);
        if (a.Status != "InProgress") throw State();
        var e = (await s.One<Execution>("SELECT * FROM job_execution WHERE appointment_id=@id FOR UPDATE", new { id }))!;
        Rules.Revision(request.ExpectedRevision, e.Revision);
        await s.Exec("UPDATE job_execution SET expected_completion=@end,revision=revision+1 WHERE id=@id", new { e.Id, end });
        await s.Exec("INSERT INTO execution_report VALUES(@report,@executionId,@actor,@end,@reason,@now)", new { report = Guid.NewGuid(), executionId = e.Id, actor = actor.Id, end, reason = request.Reason, now = Now });
        a = (await s.One<Appointment>("UPDATE appointment SET revision=revision+1 WHERE id=@id RETURNING *", new { id }))!;
        await Reconcile(s, a, e, end, false);
        await s.Event(a, "ProgressReported", [DemoIds.Staff], Now, new { expectedCompletion = end, request.Reason });
        var view = await BookingService.View(s, a);
        await s.Commit();
        return view;
    }

    public async Task<AppointmentView> Complete(Guid id, ChangeRequest request, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        Rules.Reason(request.Reason);
        await using var s = await database.Open(ct);
        await s.Begin();
        var a = await BookingService.LockAppointment(s, id, actor);
        if (a.Status == "Completed") return await BookingService.View(s, a);
        if (a.Status != "InProgress") throw State();
        var e = (await s.One<Execution>("SELECT * FROM job_execution WHERE appointment_id=@id FOR UPDATE", new { id }))!;
        Rules.Revision(request.ExpectedRevision, e.Revision);
        var now = Now;
        if (now < e.ActualStart) throw new DomainError(400, "INVALID_COMPLETION", "Completion cannot precede actual start.");
        await s.Exec("UPDATE job_execution SET actual_completion=@now,revision=revision+1 WHERE id=@id", new { e.Id, now });
        await s.Exec("UPDATE resource_usage SET released_at=@now WHERE execution_id=@id AND released_at IS NULL", new { e.Id, now });
        a = (await s.One<Appointment>("UPDATE appointment SET status='Completed',revision=revision+1 WHERE id=@id RETURNING *", new { id }))!;
        await Reconcile(s, a, e, now, true);
        await s.Event(a, "WorkCompleted", [a.CustomerId, DemoIds.Staff], now, new { actualCompletion = now, request.Reason });
        // Scheduled capacity remains reserved until its original end. Early release is a future policy.
        var view = await BookingService.View(s, a);
        await s.Commit();
        return view;
    }

    private async Task Reconcile(Session s, Appointment a, Execution e, DateTime end, bool completed)
    {
        // Forecast reporting remains valid even if it intersects planned reservations.
        // Shared resource locks serialize this query with new booking/cancellation commits.
        var affected = await s.Many<Appointment>("""
            SELECT DISTINCT b.* FROM appointment b JOIN resource_reservation r ON r.appointment_id=b.id AND r.is_active
            JOIN resource_usage u ON u.resource_id=r.resource_id AND u.execution_id=@executionId
            WHERE b.id<>@id AND b.status='Confirmed' AND b.starts_at<@end AND b.ends_at>@actualStart
            """, new { id = a.Id, executionId = e.Id, end, actualStart = e.ActualStart });
        await s.Exec("UPDATE disruption_incident SET status='Resolved',updated_at=@now WHERE execution_id=@id AND status='Open'", new { e.Id, now = Now });
        foreach (var b in affected)
        {
            // Completion releases occupancy, but a now-late job still needs its full work duration.
            var unresolved = !completed || end.AddMinutes(b.EstimatedMinutes) > b.EndsAt;
            var reason = completed ? "Actual overrun leaves insufficient time in the accepted reservation." : "Expected completion overlaps the accepted reservation.";
            await s.Exec("""
                INSERT INTO disruption_incident(id,execution_id,affected_appointment_id,reason,status,updated_at)
                VALUES(@incident,@executionId,@affected,@reason,@status,@now)
                ON CONFLICT(execution_id,affected_appointment_id) DO UPDATE SET reason=EXCLUDED.reason,status=EXCLUDED.status,updated_at=EXCLUDED.updated_at
                """, new { incident = Guid.NewGuid(), executionId = e.Id, affected = b.Id, reason, status = unresolved ? "Open" : "Resolved", now = Now });
        }
        var outsideShift = await s.Scalar<bool>("""
            SELECT EXISTS(SELECT 1 FROM resource_usage u WHERE u.execution_id=@id
            AND NOT EXISTS(SELECT 1 FROM availability w WHERE w.resource_id=u.resource_id AND w.starts_at<=@start AND w.ends_at>=@end))
            """, new { e.Id, start = e.ActualStart, end });
        if (affected.Count > 0 || outsideShift)
            await s.Event(a, "OperationalImpact", [DemoIds.Staff], Now,
                new { affectedAppointments = affected.Select(x => x.Id).ToArray(), outsideShift, completed, expectedOrActualCompletion = end });
    }

    public async Task<object> Impacts(Guid id, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await database.Open(ct);
        var a = await BookingService.Find(s, id);
        actor.Owns(a.CustomerId, a.DealershipId);
        var incidents = await s.Many<Incident>("""
            SELECT i.* FROM disruption_incident i JOIN job_execution e ON e.id=i.execution_id
            WHERE e.appointment_id=@id OR i.affected_appointment_id=@id ORDER BY i.updated_at DESC
            """, new { id });
        var execution = await s.One<Execution>("SELECT * FROM job_execution WHERE appointment_id=@id", new { id });
        return new { incidents, staleForecast = execution is { ActualCompletion: null } && execution.ExpectedCompletion <= Now };
    }

    private static DomainError State() => new(409, "APPOINTMENT_STATE_CONFLICT", "The appointment is not in the required lifecycle state.");
}
