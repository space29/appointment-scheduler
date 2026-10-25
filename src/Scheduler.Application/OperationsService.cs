using Scheduler.Domain;

namespace Scheduler.Application;

public sealed class OperationsService(ISchedulingStore store, TimeProvider clock, IStaffDirectory staff)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<AppointmentView> Start(Guid id, ChangeRequest request, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await store.Open(ct);
        await s.Begin();
        var appointment = await AppointmentWorkflow.Lock(s, id, actor);
        var now = Now;
        if (!appointment.Start(request.ExpectedRevision, request.Reason, await s.ResourcesBlocked(id), now)) return await s.View(appointment);
        await s.Save(appointment);
        await s.AcquireResources(appointment.Execution!.Snapshot);
        await s.AddExecutionReport(appointment.Execution.Snapshot, actor.Id, request.Reason, now);
        var view = await s.View(appointment);
        await s.Commit();
        return view;
    }

    public async Task<AppointmentView> Progress(Guid id, ProgressRequest request, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await store.Open(ct);
        await s.Begin();
        var appointment = await AppointmentWorkflow.Lock(s, id, actor);
        var now = Now;
        appointment.ReportProgress(request.ExpectedRevision, request.ExpectedCompletion.UtcDateTime, request.Reason, now);
        await s.Save(appointment);
        await s.AddExecutionReport(appointment.Execution!.Snapshot, actor.Id, request.Reason, now);
        await Reconcile(s, appointment, request.ExpectedCompletion.UtcDateTime, false, now);
        await AppointmentWorkflow.Events(s, appointment, staff);
        var view = await s.View(appointment);
        await s.Commit();
        return view;
    }

    public async Task<AppointmentView> Complete(Guid id, ChangeRequest request, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await store.Open(ct);
        await s.Begin();
        var appointment = await AppointmentWorkflow.Lock(s, id, actor);
        var now = Now;
        if (!appointment.Complete(request.ExpectedRevision, request.Reason, now)) return await s.View(appointment);
        await s.Save(appointment);
        await s.ReleaseUsage(appointment.Execution!.Snapshot.Id, now);
        await Reconcile(s, appointment, now, true, now);
        await AppointmentWorkflow.Events(s, appointment, staff);
        var view = await s.View(appointment);
        await s.Commit();
        return view;
    }

    private static async Task Reconcile(ISchedulingSession s, ServiceAppointment appointment, DateTime end, bool completed, DateTime now)
    {
        var affected = await s.Affected(appointment, end);
        var decisions = affected.Select(b => new ImpactDecision(b.Id, OverrunPolicy.RequiresRecovery(b, end, completed),
            completed ? "Actual overrun leaves insufficient time in the accepted reservation." : "Expected completion overlaps the accepted reservation.")).ToArray();
        await s.ReconcileImpacts(appointment.Execution!.Snapshot.Id, decisions, now);
        var outsideShift = await s.OutsideShift(appointment.Execution.Snapshot, end);
        appointment.RecordImpact(affected.Select(x => x.Id).ToArray(), outsideShift, completed, end, now);
    }

    public async Task<ImpactView> Impacts(Guid id, Actor actor, CancellationToken ct)
    {
        actor.RequireStaff();
        await using var s = await store.Open(ct);
        var appointment = await AppointmentWorkflow.Find(s, id);
        actor.Owns(appointment.Snapshot.CustomerId, appointment.Snapshot.DealershipId);
        return new(await s.Impacts(id), appointment.Execution?.HasStaleForecast(Now) ?? false);
    }
}
