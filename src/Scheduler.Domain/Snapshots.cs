namespace Scheduler.Domain;

// Immutable persistence snapshots. Aggregate state is changed only by domain methods.
public sealed record QuoteSnapshot
{
    public Guid Id { get; init; }
    public Guid CustomerId { get; init; }
    public Guid VehicleId { get; init; }
    public Guid DealershipId { get; init; }
    public Guid ServiceTypeId { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    public int EstimatedMinutes { get; init; }
    public int BufferMinutes { get; init; }
    public int DurationVersion { get; init; }
    public decimal BasePrice { get; init; }
    public decimal Multiplier { get; init; }
    public string Band { get; init; } = "Default";
    public decimal Total { get; init; }
    public string Currency { get; init; } = "USD";
    public int PriceVersion { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
}
public sealed record AppointmentSnapshot
{
    public Guid Id { get; init; }
    public Guid QuoteId { get; init; }
    public Guid CustomerId { get; init; }
    public Guid VehicleId { get; init; }
    public Guid DealershipId { get; init; }
    public Guid ServiceTypeId { get; init; }
    public DateTime StartsAt { get; init; }
    public DateTime EndsAt { get; init; }
    public DateTime OriginalStartsAt { get; init; }
    public DateTime OriginalEndsAt { get; init; }
    public int EstimatedMinutes { get; init; }
    public int BufferMinutes { get; init; }
    public int DurationVersion { get; init; }
    public int PriceVersion { get; init; }
    public decimal Total { get; init; }
    public string Currency { get; init; } = "";
    public string PaymentTiming { get; init; } = "";
    public string BookingChannel { get; init; } = "";
    public string Status { get; init; } = "";
    public int Revision { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? CancelledAt { get; init; }
    public string? CancellationReason { get; init; }
    public Guid? CancelledBy { get; init; }
}
public sealed record ExecutionSnapshot
{
    public Guid Id { get; init; }
    public Guid AppointmentId { get; init; }
    public DateTime ActualStart { get; init; }
    public DateTime ExpectedCompletion { get; init; }
    public DateTime? ActualCompletion { get; init; }
    public int Revision { get; init; }
}
