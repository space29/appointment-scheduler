using Scheduler.Domain;

namespace Scheduler.Application;

public static class Projection
{
    public static Quote ToDto(this QuoteSnapshot s) => new()
    {
        Id = s.Id,
        CustomerId = s.CustomerId,
        VehicleId = s.VehicleId,
        DealershipId = s.DealershipId,
        ServiceTypeId = s.ServiceTypeId,
        StartsAt = s.StartsAt,
        EndsAt = s.EndsAt,
        EstimatedMinutes = s.EstimatedMinutes,
        BufferMinutes = s.BufferMinutes,
        DurationVersion = s.DurationVersion,
        BasePrice = s.BasePrice,
        Multiplier = s.Multiplier,
        Band = s.Band,
        Total = s.Total,
        Currency = s.Currency,
        PriceVersion = s.PriceVersion,
        CreatedAt = s.CreatedAt,
        ExpiresAt = s.ExpiresAt,
    };
    public static Appointment ToDto(this AppointmentSnapshot s) => new()
    {
        Id = s.Id,
        QuoteId = s.QuoteId,
        CustomerId = s.CustomerId,
        VehicleId = s.VehicleId,
        DealershipId = s.DealershipId,
        ServiceTypeId = s.ServiceTypeId,
        StartsAt = s.StartsAt,
        EndsAt = s.EndsAt,
        OriginalStartsAt = s.OriginalStartsAt,
        OriginalEndsAt = s.OriginalEndsAt,
        EstimatedMinutes = s.EstimatedMinutes,
        BufferMinutes = s.BufferMinutes,
        DurationVersion = s.DurationVersion,
        PriceVersion = s.PriceVersion,
        Total = s.Total,
        Currency = s.Currency,
        PaymentTiming = s.PaymentTiming,
        BookingChannel = s.BookingChannel,
        Status = s.Status,
        Revision = s.Revision,
        CreatedAt = s.CreatedAt,
        CancelledAt = s.CancelledAt,
        CancellationReason = s.CancellationReason,
        CancelledBy = s.CancelledBy,
    };
    public static Execution ToDto(this ExecutionSnapshot s) => new()
    {
        Id = s.Id,
        AppointmentId = s.AppointmentId,
        ActualStart = s.ActualStart,
        ExpectedCompletion = s.ExpectedCompletion,
        ActualCompletion = s.ActualCompletion,
        Revision = s.Revision,
    };
}
