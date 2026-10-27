using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Scheduler.Application;
using Scheduler.Infrastructure;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class CachingScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_warm_policy_terms_When_configuration_expires_Then_new_quotes_refresh_and_existing_offers_remain_unchanged()
    {
        // Given a warm weekday peak policy and then an administrator's database update.
        using var customer = Db.Client();
        var monday = Db.Slot;
        while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(1);
        await Db.Sql("UPDATE availability SET starts_at=@start,ends_at=@end",
            new { start = monday.AddHours(-2).UtcDateTime, end = monday.AddHours(8).UtcDateTime });
        var original = await GivenAValidQuote(customer, monday);
        original.Total.Should().Be(120);
        await Db.Sql("""
            UPDATE price_policy SET base_price=200,version=2;
            UPDATE duration_rule SET estimated_minutes=90,version=2 WHERE category='Standard';
            UPDATE price_band SET multiplier=1.50 WHERE name='Peak';
            """);
        var warm = await GivenAValidQuote(customer, monday);
        warm.Total.Should().Be(original.Total);
        warm.EstimatedMinutes.Should().Be(original.EstimatedMinutes);

        // When the policy's sixty-second cache lifetime expires.
        Db.Clock.Set(Db.Baseline.AddSeconds(60));
        var refreshed = await GivenAValidQuote(customer, monday);
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(original.Id));
        var booked = await ReadSuccessful<AppointmentView>(response);

        // Then new offers use one refreshed base-price/band/duration snapshot; an existing valid offer keeps its terms.
        refreshed.Total.Should().Be(300);
        refreshed.EstimatedMinutes.Should().Be(90);
        refreshed.PriceVersion.Should().Be(2);
        refreshed.DurationVersion.Should().Be(2);
        booked.Appointment.Total.Should().Be(120);
        booked.Appointment.EstimatedMinutes.Should().Be(60);
        booked.Appointment.PriceVersion.Should().Be(original.PriceVersion);
    }

    [Fact]
    public async Task Given_a_warm_shared_catalog_When_vehicle_ownership_changes_Then_customer_data_stays_fresh_and_catalog_data_expires()
    {
        // Given a shared catalogue warmed by customer A.
        using var first = Db.Client();
        using var second = Db.Client("customer-b");
        var original = await first.GetFromJsonAsync<JsonElement>("/reference-data");
        var serviceName = original.GetProperty("services")[0].GetProperty("name").GetString();
        await Db.Sql("""
            UPDATE service_type SET name='Updated inspection';
            UPDATE vehicle SET customer_id=@customer WHERE id=@vehicle;
            """, new { customer = DemoIds.CustomerB, vehicle = VehicleA });

        // When both customers read during the catalogue lifetime, then again after expiry.
        var warmFirst = await first.GetFromJsonAsync<JsonElement>("/reference-data");
        var warmSecond = await second.GetFromJsonAsync<JsonElement>("/reference-data");
        Db.Clock.Set(Db.Baseline.AddSeconds(300));
        var refreshed = await first.GetFromJsonAsync<JsonElement>("/reference-data");

        // Then sharing catalogue data never shares a customer's vehicle list or delays its authorization changes.
        warmFirst.GetProperty("vehicles").GetArrayLength().Should().Be(0);
        warmSecond.GetProperty("vehicles").EnumerateArray().Select(v => v.GetProperty("id").GetGuid()).Should().Contain(VehicleA);
        warmFirst.GetProperty("services")[0].GetProperty("name").GetString().Should().Be(serviceName);
        refreshed.GetProperty("services")[0].GetProperty("name").GetString().Should().Be("Updated inspection");
    }

    [Fact]
    public async Task Given_a_warm_quote_policy_When_vehicle_ownership_is_revoked_Then_another_quote_is_rejected()
    {
        // Given a valid quote has warmed the pricing/duration policy cache.
        using var customer = Db.Client();
        await GivenAValidQuote(customer);
        await Db.Sql("UPDATE vehicle SET customer_id=@customer WHERE id=@vehicle", new { customer = DemoIds.CustomerB, vehicle = VehicleA });

        // When the old owner requests another quote.
        using var response = await customer.PostAsJsonAsync("/appointment-quotes", QuoteDetails());

        // Then a cached policy cannot substitute for current vehicle ownership.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("INVALID_REFERENCE");
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment_quote")).Should().Be(1);
    }

    [Theory]
    [InlineData("Large", 75, 10)]
    [InlineData("Unknown", 60, 0)]
    public async Task Given_a_warm_policy_When_the_vehicle_category_changes_Then_the_current_category_selects_the_estimate(string category, int minutes, int buffer)
    {
        // Given the Standard category has already warmed the shared policy snapshot.
        using var customer = Db.Client();
        await GivenAValidQuote(customer);
        await Db.Sql("UPDATE vehicle SET category=@category WHERE id=@id", new { category, id = VehicleA });

        // When quoting the same vehicle again without invalidating shared rules.
        var quote = await GivenAValidQuote(customer);

        // Then current category selection and baseline fallback remain correct.
        quote.EstimatedMinutes.Should().Be(minutes);
        quote.BufferMinutes.Should().Be(buffer);
    }
}
