using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Scheduler.Application;
using Scheduler.Infrastructure;
using Xunit;

namespace Scheduler.Tests;

// Shared Given steps assert successful setup so a failed precondition cannot disguise an outcome.
// Each scenario keeps its When action and business assertions visible in the test itself.
public abstract class SchedulerScenario(DatabaseFixture database) : IAsyncLifetime
{
    protected DatabaseFixture Db { get; } = database;
    protected static readonly Guid VehicleA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    protected static readonly Guid VehicleB = Guid.Parse("20000000-0000-0000-0000-000000000002");

    public Task InitializeAsync() => Db.Reset();
    public Task DisposeAsync() => Task.CompletedTask;

    protected QuoteRequest QuoteDetails(DateTimeOffset? start = null, bool customerB = false) => new(
        customerB ? DemoIds.CustomerB : DemoIds.CustomerA, customerB ? VehicleB : VehicleA,
        DemoIds.Dealership, DemoIds.Service, start ?? Db.Slot);

    protected async Task<Quote> GivenAValidQuote(HttpClient customer, DateTimeOffset? start = null, bool customerB = false)
    {
        using var response = await customer.PostAsJsonAsync("/appointment-quotes", QuoteDetails(start, customerB));
        return await ReadSuccessful<Quote>(response);
    }

    protected async Task<AppointmentView> GivenAConfirmedBooking(HttpClient customer, DateTimeOffset? start = null)
    {
        var quote = await GivenAValidQuote(customer, start);
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));
        response.StatusCode.Should().Be(HttpStatusCode.Created, "the scenario requires a newly confirmed appointment");
        return await ReadSuccessful<AppointmentView>(response);
    }

    protected Task GivenOnlyOneAvailablePair() => Db.Sql("""
        DELETE FROM availability WHERE resource_id IN (
        '70000000-0000-0000-0000-000000000002','80000000-0000-0000-0000-000000000002')
        """);

    protected async Task GivenWorkHasStarted(HttpClient staff, AppointmentView booking)
    {
        Db.Clock.Set(new DateTimeOffset(booking.Appointment.StartsAt));
        using var response = await staff.PostAsJsonAsync($"/appointments/{booking.Appointment.Id}/start",
            new ChangeRequest(booking.Appointment.Revision, "Customer checked in"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "work must be in progress before the scenario action");
    }

    protected async Task GivenAnOverrun(HttpClient staff, AppointmentView booking, DateTimeOffset expectedCompletion)
    {
        using var response = await staff.PatchAsJsonAsync($"/appointments/{booking.Appointment.Id}/progress",
            new ProgressRequest(1, expectedCompletion, "Inspection requires more time"));
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the overrun must be recorded as a scenario precondition");
    }

    protected async Task GivenACancelledBooking(HttpClient customer, AppointmentView booking)
    {
        using var response = await customer.PostAsJsonAsync($"/appointments/{booking.Appointment.Id}/cancel",
            new ChangeRequest(booking.Appointment.Revision, "Customer changed plans"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    protected static async Task<T> ReadSuccessful<T>(HttpResponseMessage response)
    {
        response.IsSuccessStatusCode.Should().BeTrue("the API should succeed, but returned {0}: {1}",
            response.StatusCode, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<T>();
        body.Should().NotBeNull();
        return body!;
    }

    protected static async Task<AppointmentView> GetBooking(HttpClient customer, Guid id)
    {
        using var response = await customer.GetAsync($"/appointments/{id}");
        return await ReadSuccessful<AppointmentView>(response);
    }
}
