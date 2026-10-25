namespace Scheduler.Application;

public sealed record QuoteRequest(Guid CustomerId, Guid VehicleId, Guid DealershipId, Guid ServiceTypeId, DateTimeOffset StartsAt);
public sealed record BookingRequest(Guid QuoteId, string PaymentTiming = "PayAtCheckIn");
public sealed record ChangeRequest(int ExpectedRevision, string Reason);
public sealed record ProgressRequest(int ExpectedRevision, DateTimeOffset ExpectedCompletion, string Reason);
public sealed class Quote
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid VehicleId { get; set; }
    public Guid DealershipId { get; set; }
    public Guid ServiceTypeId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public int EstimatedMinutes { get; set; }
    public int BufferMinutes { get; set; }
    public int DurationVersion { get; set; }
    public decimal BasePrice { get; set; }
    public decimal Multiplier { get; set; }
    public string Band { get; set; } = "Default";
    public decimal Total { get; set; }
    public string Currency { get; set; } = "USD";
    public int PriceVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string[] PaymentOptions => ["PayAtCheckIn"];
}
public sealed class Appointment
{
    public Guid Id { get; set; }
    public Guid QuoteId { get; set; }
    public Guid CustomerId { get; set; }
    public Guid VehicleId { get; set; }
    public Guid DealershipId { get; set; }
    public Guid ServiceTypeId { get; set; }
    public DateTime StartsAt { get; set; }
    public DateTime EndsAt { get; set; }
    public DateTime OriginalStartsAt { get; set; }
    public DateTime OriginalEndsAt { get; set; }
    public int EstimatedMinutes { get; set; }
    public int BufferMinutes { get; set; }
    public int DurationVersion { get; set; }
    public int PriceVersion { get; set; }
    public decimal Total { get; set; }
    public string Currency { get; set; } = "";
    public string PaymentTiming { get; set; } = "";
    public string BookingChannel { get; set; } = "";
    public string Status { get; set; } = "";
    public int Revision { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CancelledAt { get; set; }
    public string? CancellationReason { get; set; }
    public Guid? CancelledBy { get; set; }
}
public sealed class Execution
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public DateTime ActualStart { get; set; }
    public DateTime ExpectedCompletion { get; set; }
    public DateTime? ActualCompletion { get; set; }
    public int Revision { get; set; }
}
public sealed record Reservation(Guid ResourceId, string Role, DateTime StartsAt, DateTime EndsAt, bool IsActive);
public sealed record Incident(Guid Id, Guid ExecutionId, Guid AffectedAppointmentId, string Reason, string Status, DateTime UpdatedAt);
public sealed record AppointmentView(Appointment Appointment, IReadOnlyList<Reservation> Resources, Execution? Execution, bool AtRisk);
public sealed record BookingResult(AppointmentView Booking, bool Created);
