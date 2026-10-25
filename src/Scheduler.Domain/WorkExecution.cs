namespace Scheduler.Domain;

// An entity owned by the appointment aggregate. Its revision tracks independent staff forecasts.
public sealed class WorkExecution
{
    public ExecutionSnapshot Snapshot { get; private set; }
    private WorkExecution(ExecutionSnapshot snapshot) => Snapshot = snapshot;
    internal static WorkExecution Restore(ExecutionSnapshot snapshot) => new(snapshot);
    internal static WorkExecution Start(Guid appointmentId, DateTime now, int workMinutes) => new(new ExecutionSnapshot
    {
        Id = Guid.NewGuid(), AppointmentId = appointmentId, ActualStart = now,
        ExpectedCompletion = now.AddMinutes(workMinutes), Revision = 1
    });
    internal void Report(int revision, DateTime end, DateTime now)
    {
        if (end <= now || end > now.AddDays(7))
            throw new DomainError(RuleFailure.Validation, "INVALID_FORECAST", "Expected completion must be in the future and within seven days.");
        Rules.Revision(revision, Snapshot.Revision);
        Snapshot = Snapshot with { ExpectedCompletion = end, Revision = Snapshot.Revision + 1 };
    }
    internal void Complete(int revision, DateTime now)
    {
        Rules.Revision(revision, Snapshot.Revision);
        if (now < Snapshot.ActualStart)
            throw new DomainError(RuleFailure.Validation, "INVALID_COMPLETION", "Completion cannot precede actual start.");
        Snapshot = Snapshot with { ActualCompletion = now, Revision = Snapshot.Revision + 1 };
    }
    public bool HasStaleForecast(DateTime now) => Snapshot.ActualCompletion is null && Snapshot.ExpectedCompletion <= now;
}
