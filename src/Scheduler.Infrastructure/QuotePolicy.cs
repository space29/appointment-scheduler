using System.Collections.Immutable;
using System.Text.Json;

namespace Scheduler.Infrastructure;

// One immutable snapshot keeps base price/version, bands, baseline, and duration rules together.
internal sealed record QuotePolicy(Guid Id, decimal BasePrice, string Currency, int Version, string TimeZone,
    int BaselineMinutes, ImmutableArray<DurationSetting> Durations, ImmutableArray<BandSetting> Bands)
{
    internal sealed class Row
    {
        public Guid PolicyId { get; set; }
        public decimal BasePrice { get; set; }
        public string Currency { get; set; } = "";
        public int PriceVersion { get; set; }
        public string TimeZone { get; set; } = "";
        public int BaselineMinutes { get; set; }
        public string DurationRules { get; set; } = "[]";
        public string PriceBands { get; set; } = "[]";
        public QuotePolicy Snapshot() => new(PolicyId, BasePrice, Currency, PriceVersion, TimeZone, BaselineMinutes,
            [.. JsonSerializer.Deserialize<DurationSetting[]>(DurationRules)!], [.. JsonSerializer.Deserialize<BandSetting[]>(PriceBands)!]);
    }
}
internal sealed record DurationSetting(string Category, int EstimatedMinutes, int BufferMinutes, int Version);
internal sealed record BandSetting(string Name, int Weekday, int StartMinute, int EndMinute, decimal Multiplier);
