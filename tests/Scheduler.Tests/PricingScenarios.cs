using System.Net.Http.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Scheduler.Api;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class PricingScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_an_unexpired_quote_and_changed_policies_When_confirmed_Then_the_quoted_terms_are_preserved()
    {
        // Given an accepted offer whose pricing and duration policies have since changed.
        using var customer = Db.Client();
        var quote = await GivenAValidQuote(customer);
        await Db.Sql("UPDATE price_policy SET base_price=999,version=2; UPDATE duration_rule SET estimated_minutes=150,version=2");

        // When the customer confirms that still-valid offer.
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));
        var booking = await ReadSuccessful<AppointmentView>(response);

        // Then the original price/duration and default payment/channel become the persisted agreement.
        using var scope = new AssertionScope();
        quote.EstimatedMinutes.Should().Be(60);
        quote.BufferMinutes.Should().Be(10);
        booking.Appointment.Total.Should().Be(quote.Total);
        booking.Appointment.EstimatedMinutes.Should().Be(60);
        booking.Appointment.BufferMinutes.Should().Be(10);
        booking.Appointment.PriceVersion.Should().Be(quote.PriceVersion);
        booking.Appointment.DurationVersion.Should().Be(quote.DurationVersion);
        booking.Appointment.PaymentTiming.Should().Be("PayAtCheckIn");
        booking.Appointment.BookingChannel.Should().Be("Web");
        booking.Resources.Should().HaveCount(2);
        (await Db.Scalar<int>("SELECT count(*)::int FROM outbox_message")).Should().Be(1);
    }

    [Theory]
    [InlineData("Large", 75)]
    [InlineData("Other", 60)]
    public async Task Given_a_vehicle_category_When_requesting_a_quote_Then_its_estimate_or_the_baseline_is_used(string category, int expectedMinutes)
    {
        // Given a vehicle with either a matching duration rule or an unknown category.
        using var customer = Db.Client("customer-b");
        await Db.Sql("UPDATE vehicle SET category=@category WHERE id=@id", new { category, id = VehicleB });

        // When a service is quoted for that vehicle.
        using var response = await customer.PostAsJsonAsync("/appointment-quotes", QuoteDetails(customerB: true));
        var quote = await ReadSuccessful<Quote>(response);

        // Then the category-specific estimate is used, with a baseline fallback when needed.
        quote.EstimatedMinutes.Should().Be(expectedMinutes);
    }

    [Theory]
    [InlineData(59, 100, "Default")]
    [InlineData(60, 120, "Peak")]
    [InlineData(240, 100, "Default")]
    [InlineData(360, 90, "OffPeak")]
    public async Task Given_configured_price_bands_When_quoting_at_a_local_boundary_Then_the_correct_band_applies(
        int minutesAfterOpening, decimal expectedPrice, string expectedBand)
    {
        // Given weekday bands interpreted in the dealership's zone, opening at 08:00 local.
        using var customer = Db.Client();
        var mondayOpening = Db.Baseline;
        while (mondayOpening.DayOfWeek != DayOfWeek.Monday) mondayOpening = mondayOpening.AddDays(1);
        var start = mondayOpening.AddMinutes(minutesAfterOpening);

        // When a quote is requested at the band boundary.
        using var response = await customer.PostAsJsonAsync("/appointment-quotes", QuoteDetails(start));
        var quote = await ReadSuccessful<Quote>(response);

        // Then start-inclusive/end-exclusive bands determine the offer.
        using var scope = new AssertionScope();
        quote.Total.Should().Be(expectedPrice);
        quote.Band.Should().Be(expectedBand);
    }
}
