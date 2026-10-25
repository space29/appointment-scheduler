namespace Scheduler.Domain;

public enum RuleFailure { Validation, Conflict }
public sealed class DomainError(RuleFailure kind, string code, string message) : Exception(message)
{
    public RuleFailure Kind { get; } = kind;
    public string Code { get; } = code;
}

public sealed record ServiceInterval
{
    public DateTime Start { get; }
    public DateTime End { get; }
    public ServiceInterval(DateTime start, DateTime end)
    {
        if (start.Kind != DateTimeKind.Utc || end.Kind != DateTimeKind.Utc || end <= start)
            throw new DomainError(RuleFailure.Validation, "INVALID_INTERVAL", "A reservation requires a positive interval in UTC.");
        Start = start;
        End = end;
    }
    public bool Overlaps(ServiceInterval other) => Start < other.End && other.Start < End;
    public bool CanFitWork(DateTime actualStart, int minutes) => actualStart >= Start && actualStart.AddMinutes(minutes) <= End;
}

public sealed record Money
{
    public decimal Amount { get; }
    public string Currency { get; }
    public Money(decimal amount, string currency)
    {
        if (amount < 0 || currency.Length != 3 || !currency.All(char.IsAsciiLetterUpper))
            throw new DomainError(RuleFailure.Validation, "INVALID_PRICE", "A price requires a nonnegative amount and a three-letter currency.");
        Amount = decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
        Currency = currency;
    }
    public Money Apply(decimal multiplier)
    {
        if (multiplier <= 0) throw new DomainError(RuleFailure.Validation, "INVALID_PRICE", "A price multiplier must be positive.");
        return new(Amount * multiplier, Currency);
    }
}

public sealed record ServiceEstimate
{
    public int WorkMinutes { get; }
    public int BufferMinutes { get; }
    public int Version { get; }
    public ServiceEstimate(int workMinutes, int bufferMinutes, int version)
    {
        if (workMinutes <= 0 || bufferMinutes < 0)
            throw new DomainError(RuleFailure.Validation, "INVALID_DURATION", "Work must have positive duration and a nonnegative buffer.");
        WorkMinutes = workMinutes;
        BufferMinutes = bufferMinutes;
        Version = version;
    }
    public ServiceInterval ReserveFrom(DateTime start) => new(start, start.AddMinutes(WorkMinutes + BufferMinutes));
}

public static class Rules
{
    public static bool Overlaps(DateTime start, DateTime end, DateTime otherStart, DateTime otherEnd) =>
        new ServiceInterval(start, end).Overlaps(new(otherStart, otherEnd));
    public static decimal Price(decimal basePrice, decimal multiplier) =>
        decimal.Round(basePrice * multiplier, 2, MidpointRounding.AwayFromZero);
    public static void Reason(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Length > 500)
            throw new DomainError(RuleFailure.Validation, "INVALID_REASON", "Provide a reason of 1 to 500 characters.");
    }
    public static void Revision(int expected, int actual)
    {
        if (expected != actual) throw new DomainError(RuleFailure.Conflict, "STALE_REVISION", "Reload the latest state before making this change.");
    }
}
