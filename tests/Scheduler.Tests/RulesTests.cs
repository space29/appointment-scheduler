using FluentAssertions;
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
    public void Given_two_reservation_intervals_When_checking_for_overlap_Then_only_shared_time_conflicts(
        int firstStart, int firstEnd, int secondStart, int secondEnd, bool expectedOverlap)
    {
        // Given two reservations whose end instants are excluded.
        var epoch = DateTime.UnixEpoch;
        var firstStartsAt = epoch.AddMinutes(firstStart);
        var firstEndsAt = epoch.AddMinutes(firstEnd);
        var secondStartsAt = epoch.AddMinutes(secondStart);
        var secondEndsAt = epoch.AddMinutes(secondEnd);

        // When their occupied time is compared.
        var overlaps = Rules.Overlaps(firstStartsAt, firstEndsAt, secondStartsAt, secondEndsAt);

        // Then back-to-back reservations are allowed, but shared occupied time conflicts.
        overlaps.Should().Be(expectedOverlap);
    }

    [Fact]
    public void Given_a_price_halfway_between_cents_When_quoting_Then_the_amount_rounds_away_from_zero()
    {
        // Given an amount requiring rounding to the demo currency's two decimal places.
        const decimal basePrice = 12.345m;
        const decimal multiplier = 1m;

        // When the final price is calculated.
        var quotedPrice = Rules.Price(basePrice, multiplier);

        // Then the customer sees the agreed rounding policy.
        quotedPrice.Should().Be(12.35m);
    }
}
