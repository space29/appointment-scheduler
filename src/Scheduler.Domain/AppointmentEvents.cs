namespace Scheduler.Domain;

public abstract record AppointmentEvent(AppointmentSnapshot Appointment, DateTime OccurredAt);
public sealed record AppointmentConfirmed(AppointmentSnapshot Appointment, DateTime OccurredAt) : AppointmentEvent(Appointment, OccurredAt);
public sealed record AppointmentCancelled(AppointmentSnapshot Appointment, DateTime OccurredAt) : AppointmentEvent(Appointment, OccurredAt);
public sealed record ProgressReported(AppointmentSnapshot Appointment, DateTime OccurredAt, DateTime ExpectedCompletion, string Reason) : AppointmentEvent(Appointment, OccurredAt);
public sealed record WorkCompleted(AppointmentSnapshot Appointment, DateTime OccurredAt, DateTime ActualCompletion, string Reason) : AppointmentEvent(Appointment, OccurredAt);
public sealed record OperationalImpact(AppointmentSnapshot Appointment, DateTime OccurredAt, IReadOnlyList<Guid> AffectedAppointments,
    bool OutsideShift, bool Completed, DateTime ExpectedOrActualCompletion) : AppointmentEvent(Appointment, OccurredAt);
