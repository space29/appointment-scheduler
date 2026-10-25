namespace Scheduler.Domain;

public enum AppointmentStatus { Confirmed, InProgress, Completed, Cancelled }

// Aggregate root: lifecycle state and execution can change only through business operations.
public sealed class ServiceAppointment
{
    private readonly List<AppointmentEvent> events = [];
    public AppointmentSnapshot Snapshot { get; private set; }
    public WorkExecution? Execution { get; private set; }
    public AppointmentStatus Status => Enum.Parse<AppointmentStatus>(Snapshot.Status);
    public ServiceInterval Reservation => new(Snapshot.StartsAt, Snapshot.EndsAt);
    public Money AgreedPrice => new(Snapshot.Total, Snapshot.Currency);
    public IReadOnlyList<AppointmentEvent> Events => events.AsReadOnly();

    private ServiceAppointment(AppointmentSnapshot snapshot, ExecutionSnapshot? execution)
    {
        Snapshot = snapshot;
        Execution = execution is null ? null : WorkExecution.Restore(execution);
    }
    public static ServiceAppointment Restore(AppointmentSnapshot snapshot, ExecutionSnapshot? execution) => new(snapshot, execution);

    public static ServiceAppointment Confirm(BookingOffer offer, string payment, DateTime now)
    {
        BookingOffer.EnsurePaymentSupported(payment);
        offer.EnsureAcceptable(now);
        var q = offer.Snapshot;
        var a = new ServiceAppointment(new AppointmentSnapshot
        {
            Id = Guid.NewGuid(), QuoteId = q.Id, CustomerId = q.CustomerId, VehicleId = q.VehicleId,
            DealershipId = q.DealershipId, ServiceTypeId = q.ServiceTypeId, StartsAt = q.StartsAt, EndsAt = q.EndsAt,
            OriginalStartsAt = q.StartsAt, OriginalEndsAt = q.EndsAt, EstimatedMinutes = q.EstimatedMinutes,
            BufferMinutes = q.BufferMinutes, DurationVersion = q.DurationVersion, PriceVersion = q.PriceVersion,
            Total = q.Total, Currency = q.Currency, PaymentTiming = payment, BookingChannel = "Web",
            Status = AppointmentStatus.Confirmed.ToString(), Revision = 1, CreatedAt = now
        }, null);
        a.events.Add(new AppointmentConfirmed(a.Snapshot, now));
        return a;
    }

    public bool Cancel(int revision, string reason, Guid actorId, DateTime now)
    {
        Rules.Reason(reason);
        if (Status == AppointmentStatus.Cancelled) return false;
        Rules.Revision(revision, Snapshot.Revision);
        Require(AppointmentStatus.Confirmed);
        Snapshot = Snapshot with
        {
            Status = AppointmentStatus.Cancelled.ToString(), Revision = Snapshot.Revision + 1,
            CancelledAt = now, CancelledBy = actorId, CancellationReason = reason
        };
        events.Add(new AppointmentCancelled(Snapshot, now));
        return true;
    }

    public bool Start(int revision, string reason, bool resourcesBlocked, DateTime now)
    {
        Rules.Reason(reason);
        if (Status == AppointmentStatus.InProgress) return false;
        Rules.Revision(revision, Snapshot.Revision);
        Require(AppointmentStatus.Confirmed);
        if (!Reservation.CanFitWork(now, Snapshot.EstimatedMinutes))
            throw new DomainError(RuleFailure.Conflict, "START_OUTSIDE_RESERVATION", "The full remaining work must fit the reservation. Staff recovery is required.");
        if (resourcesBlocked)
            throw new DomainError(RuleFailure.Conflict, "RESOURCE_OCCUPIED_OR_AT_RISK", "An assigned resource is occupied or this appointment has an unresolved disruption.");
        Execution = WorkExecution.Start(Snapshot.Id, now, Snapshot.EstimatedMinutes);
        Snapshot = Snapshot with { Status = AppointmentStatus.InProgress.ToString(), Revision = Snapshot.Revision + 1 };
        return true;
    }

    public void ReportProgress(int revision, DateTime expectedEnd, string reason, DateTime now)
    {
        Rules.Reason(reason);
        Require(AppointmentStatus.InProgress);
        Execution!.Report(revision, expectedEnd, now);
        Snapshot = Snapshot with { Revision = Snapshot.Revision + 1 };
        events.Add(new ProgressReported(Snapshot, now, expectedEnd, reason));
    }

    public bool Complete(int executionRevision, string reason, DateTime now)
    {
        Rules.Reason(reason);
        if (Status == AppointmentStatus.Completed) return false;
        Require(AppointmentStatus.InProgress);
        Execution!.Complete(executionRevision, now);
        Snapshot = Snapshot with { Status = AppointmentStatus.Completed.ToString(), Revision = Snapshot.Revision + 1 };
        events.Add(new WorkCompleted(Snapshot, now, now, reason));
        return true;
    }

    public void RecordImpact(IReadOnlyList<Guid> affected, bool outsideShift, bool completed, DateTime end, DateTime now)
    {
        if (affected.Count != 0 || outsideShift)
            events.Add(new OperationalImpact(Snapshot, now, affected, outsideShift, completed, end));
    }
    private void Require(AppointmentStatus expected)
    {
        if (Status != expected)
            throw new DomainError(RuleFailure.Conflict, "APPOINTMENT_STATE_CONFLICT", "The appointment is not in the required lifecycle state.");
    }
}

public static class OverrunPolicy
{
    public static bool RequiresRecovery(AppointmentSnapshot affected, DateTime finish, bool completed) =>
        !completed || !new ServiceInterval(affected.StartsAt, affected.EndsAt).CanFitWork(finish, affected.EstimatedMinutes);
}
