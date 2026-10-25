using Scheduler.Domain;

namespace Scheduler.Application;

public enum FailureType { Validation, Conflict, NotFound, Forbidden, Unauthenticated }
public sealed class RequestFailure(FailureType kind, string code, string message) : Exception(message)
{
    public FailureType Kind { get; } = kind;
    public string Code { get; } = code;
}
public class PersistenceFailure(string code, Exception inner) : Exception("Persistence operation failed.", inner)
{
    public string Code { get; } = code;
}
public sealed class ContentionFailure(string code, Exception inner) : PersistenceFailure(code, inner);
public sealed class DeliveryUnavailableException : Exception;

public sealed record Actor(Guid Id, bool IsStaff, Guid? DealershipId = null)
{
    public void Owns(Guid customerId, Guid dealershipId)
    {
        if (IsStaff ? DealershipId != dealershipId : Id != customerId)
            throw new RequestFailure(FailureType.NotFound, "NOT_FOUND", "The requested record was not found.");
    }
    public void RequireStaff()
    {
        if (!IsStaff) throw new RequestFailure(FailureType.Forbidden, "STAFF_REQUIRED", "This operation requires dealership staff.");
    }
}

public interface IStaffDirectory { Guid[] Recipients(Guid dealershipId); }
public interface ISchedulingStore { Task<ISchedulingSession> Open(CancellationToken ct); }

// Typed unit of work. The application defines required atomicity, not SQL or database-specific lock syntax.
public interface ISchedulingSession : IAsyncDisposable
{
    Task Begin();
    Task Commit();
    Task<QuoteInputs?> QuoteInputs(QuoteRequest request);
    Task<PriceBand?> PriceBand(Guid policyId, int weekday, int minute);
    Task AddQuote(QuoteSnapshot quote);
    Task<QuoteSnapshot?> Quote(Guid id, bool forUpdate = false);
    Task<ServiceAppointment?> ByQuote(Guid quoteId);
    Task<ServiceAppointment?> Appointment(Guid id, bool forUpdate = false);
    Task<Guid[]> ResourceIds(Guid appointmentId);
    Task LockResources(Guid[] ids);
    Task<List<Guid>> Candidates(QuoteSnapshot quote, string kind, DateTime now);
    Task AddBooking(ServiceAppointment appointment, Guid bayId, Guid technicianId);
    Task Save(ServiceAppointment appointment);
    Task<AppointmentView> View(ServiceAppointment appointment);
    Task ReleaseReservations(Guid appointmentId, DateTime now);
    Task ResolveCancelledImpacts(Guid appointmentId, DateTime now);
    Task<bool> ResourcesBlocked(Guid appointmentId);
    Task AcquireResources(ExecutionSnapshot execution);
    Task ReleaseUsage(Guid executionId, DateTime now);
    Task AddExecutionReport(ExecutionSnapshot execution, Guid actor, string reason, DateTime now);
    Task<List<AppointmentSnapshot>> Affected(ServiceAppointment source, DateTime end);
    Task ReconcileImpacts(Guid executionId, IReadOnlyList<ImpactDecision> decisions, DateTime now);
    Task<bool> OutsideShift(ExecutionSnapshot execution, DateTime end);
    Task<List<Incident>> Impacts(Guid appointmentId);
    Task AppendEvents(IReadOnlyList<OutboxEvent> events);
}

public sealed record OutboxEvent(AppointmentEvent Event, Guid[] Recipients);
public sealed record ImpactDecision(Guid AppointmentId, bool IsOpen, string Reason);
public sealed class QuoteInputs
{
    public int EstimatedMinutes { get; set; }
    public int BufferMinutes { get; set; }
    public int DurationVersion { get; set; }
    public Guid PolicyId { get; set; }
    public decimal BasePrice { get; set; }
    public string Currency { get; set; } = "";
    public int PriceVersion { get; set; }
    public string TimeZone { get; set; } = "";
}
public sealed record PriceBand(string Name, decimal Multiplier);
public sealed record ImpactView(IReadOnlyList<Incident> Incidents, bool StaleForecast);

public interface IReferenceDataReader
{
    Task<bool> IsReady(CancellationToken ct);
    Task<object> Read(Actor actor, CancellationToken ct);
}
public interface IDeliveryProvider { Task Send(Guid deliveryId, CancellationToken ct); }
public sealed record DeliveryClaim(Guid Id, Guid Token, int PreviousAttempts, bool Suppressed, string? TraceParent = null, string? TraceState = null);
public interface INotificationStore
{
    Task Materialize(DateTime now, CancellationToken ct);
    Task<DeliveryClaim?> Claim(DateTime now, CancellationToken ct);
    Task Acknowledge(DeliveryClaim claim, string status, string? error, DateTime next, CancellationToken ct);
    Task<List<NotificationDispatcher.InboxItem>> Inbox(Guid recipient, int limit, CancellationToken ct);
    Task<bool> Read(Guid id, Guid recipient, DateTime now, CancellationToken ct);
    Task<List<NotificationDispatcher.Delivery>> Deliveries(Guid appointmentId, CancellationToken ct);
    Task<AppointmentSnapshot?> OwnerOfDelivery(Guid deliveryId, CancellationToken ct);
    Task<bool> Retry(Guid id, DateTime now, CancellationToken ct);
}
