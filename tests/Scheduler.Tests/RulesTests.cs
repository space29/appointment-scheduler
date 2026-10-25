using Scheduler.Api;
using Xunit;

namespace Scheduler.Tests;

public sealed class RulesTests
{
    [Theory]
    [InlineData(0, 60, 60, 120, false)]
    [InlineData(0, 60, 59, 120, true)]
    [InlineData(0, 60, 10, 20, true)]
    [InlineData(10, 20, 0, 60, true)]
    [InlineData(0, 60, 0, 60, true)]
    public void IntervalsUseHalfOpenBoundaries(int a, int b, int c, int d, bool expected)
    {
        var epoch = DateTime.UnixEpoch;
        Assert.Equal(expected, Rules.Overlaps(epoch.AddMinutes(a), epoch.AddMinutes(b), epoch.AddMinutes(c), epoch.AddMinutes(d)));
    }
    [Fact]
    public void PriceRoundsAtCurrencyPrecision() => Assert.Equal(12.35m, Rules.Price(12.345m, 1m));
}
