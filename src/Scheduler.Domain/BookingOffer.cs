namespace Scheduler.Domain;

public sealed class BookingOffer
{
    public QuoteSnapshot Snapshot { get; }
    private BookingOffer(QuoteSnapshot snapshot) => Snapshot = snapshot;
    public static BookingOffer Restore(QuoteSnapshot snapshot) => new(snapshot);

    public static BookingOffer Issue(Guid customerId, Guid vehicleId, Guid dealershipId, Guid serviceTypeId,
        DateTime start, ServiceEstimate estimate, Money basePrice, decimal multiplier, string band, int priceVersion, DateTime now)
    {
        if (start <= now || start > now.AddDays(365))
            throw new DomainError(RuleFailure.Validation, "INVALID_TIME", "Choose a future start within 365 days.");
        var interval = estimate.ReserveFrom(start);
        var total = basePrice.Apply(multiplier);
        return new(new QuoteSnapshot
        {
            Id = Guid.NewGuid(), CustomerId = customerId, VehicleId = vehicleId, DealershipId = dealershipId,
            ServiceTypeId = serviceTypeId, StartsAt = interval.Start, EndsAt = interval.End,
            EstimatedMinutes = estimate.WorkMinutes, BufferMinutes = estimate.BufferMinutes, DurationVersion = estimate.Version,
            BasePrice = basePrice.Amount, Multiplier = multiplier, Band = band, Total = total.Amount,
            Currency = total.Currency, PriceVersion = priceVersion, CreatedAt = now, ExpiresAt = now.AddMinutes(10)
        });
    }
    public void EnsureAcceptable(DateTime now)
    {
        if (Snapshot.ExpiresAt <= now) throw new DomainError(RuleFailure.Conflict, "QUOTE_EXPIRED", "Obtain and accept a new quote.");
        if (Snapshot.StartsAt <= now) throw new DomainError(RuleFailure.Conflict, "START_IN_PAST", "The requested start is no longer in the future.");
    }
    public static void EnsurePaymentSupported(string payment)
    {
        if (payment != "PayAtCheckIn")
            throw new DomainError(RuleFailure.Validation, "PAYMENT_OPTION_UNAVAILABLE", "Only payment at check-in is currently supported.");
    }
}
