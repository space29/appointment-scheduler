using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scheduler.Api;

public static class DemoIds
{
    public static readonly Guid CustomerA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    public static readonly Guid CustomerB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    public static readonly Guid Dealership = Guid.Parse("30000000-0000-0000-0000-000000000001");
    public static readonly Guid Service = Guid.Parse("40000000-0000-0000-0000-000000000001");
    public static readonly Guid Staff = Guid.Parse("90000000-0000-0000-0000-000000000001");
}

public sealed record Actor(Guid Id, bool IsStaff)
{
    public void Owns(Guid customerId, Guid dealershipId)
    {
        if (IsStaff ? dealershipId != DemoIds.Dealership : Id != customerId)
            throw new DomainError(404, "NOT_FOUND", "The requested record was not found.");
    }
    public void RequireStaff()
    {
        if (!IsStaff) throw new DomainError(403, "STAFF_REQUIRED", "This operation requires dealership staff.");
    }
}

public sealed class DomainError(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

public static class Rules
{
    public static bool Overlaps(DateTime start, DateTime end, DateTime otherStart, DateTime otherEnd) => start < otherEnd && otherStart < end;
    public static decimal Price(decimal basePrice, decimal multiplier) => decimal.Round(basePrice * multiplier, 2, MidpointRounding.AwayFromZero);
    public static void Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new DomainError(400, "INVALID_REASON", "Provide a reason of 1 to 500 characters.");
    }
    public static void Revision(int expected, int actual)
    {
        if (expected != actual) throw new DomainError(409, "STALE_REVISION", "Reload the latest state before making this change.");
    }
}

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

public static class Telemetry
{
    public static readonly ActivitySource Activities = new("Scheduler");
    public static readonly Meter Meter = new("Scheduler");
    public static readonly Counter<long> Bookings = Meter.CreateCounter<long>("scheduler.booking.outcomes");
    public static readonly Histogram<double> BookingLatency = Meter.CreateHistogram<double>("scheduler.booking.duration", "ms");
    public static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("scheduler.notification.outcomes");
}

// DateTimeOffset's normal parser also accepts missing offsets. The API requires an explicit instant.
public sealed class ExplicitOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var text = reader.GetString();
        var t = text?.IndexOf('T') ?? -1;
        if (t < 0 || !(text!.EndsWith('Z') || text[(t + 1)..].Contains('+') || text[(t + 1)..].Contains('-')) ||
            !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var result))
            throw new JsonException("An ISO 8601 timestamp with an explicit offset is required.");
        return result;
    }
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
