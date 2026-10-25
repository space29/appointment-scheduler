using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Scheduler.Api;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class OperationsScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_a_confirmed_booking_When_cancellation_is_retried_Then_capacity_is_released_once_and_history_remains()
    {
        // Given a booking occupying the final available pair.
        await GivenOnlyOneAvailablePair();
        using var customer = Db.Client();
        var booking = await GivenAConfirmedBooking(customer);
        var request = new ChangeRequest(booking.Appointment.Revision, "Customer changed plans");

        // When the customer cancels and retries the same command.
        using var cancellation = await customer.PostAsJsonAsync($"/appointments/{booking.Appointment.Id}/cancel", request);
        using var replay = await customer.PostAsJsonAsync($"/appointments/{booking.Appointment.Id}/cancel", request);
        var cancelled = await ReadSuccessful<AppointmentView>(replay);

        // Then history remains, both reservations are released, and one cancellation event is emitted.
        using (new AssertionScope())
        {
            cancellation.StatusCode.Should().Be(HttpStatusCode.OK);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            cancelled.Appointment.Status.Should().Be("Cancelled");
            cancelled.Resources.Should().HaveCount(2).And.OnlyContain(r => !r.IsActive);
            (await Db.Scalar<int>("SELECT count(*)::int FROM resource_reservation WHERE NOT is_active")).Should().Be(2);
            (await Db.Scalar<int>("SELECT count(*)::int FROM outbox_message WHERE event_type='AppointmentCancelled'")).Should().Be(1);
        }

        // When a fresh offer requests the released slot.
        var replacementQuote = await GivenAValidQuote(customer);
        using var replacement = await customer.PostAsJsonAsync("/appointments", new BookingRequest(replacementQuote.Id));

        // Then that capacity can be booked again.
        replacement.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Given_work_can_start_When_start_and_cancellation_race_Then_only_one_transition_wins()
    {
        // Given a confirmed appointment at its agreed start time.
        using var customer = Db.Client();
        using var staff = Db.Client("staff", second: true);
        var booking = await GivenAConfirmedBooking(customer);
        Db.Clock.Set(Db.Slot);
        var request = new ChangeRequest(1, "Concurrent lifecycle decision");

        // When the customer cancels while staff starts the work through another instance.
        var responses = await Task.WhenAll(
            customer.PostAsJsonAsync($"/appointments/{booking.Appointment.Id}/cancel", request),
            staff.PostAsJsonAsync($"/appointments/{booking.Appointment.Id}/start", request));
        var current = await GetBooking(customer, booking.Appointment.Id);

        // Then one transition is rejected and actual occupancy matches the winning state.
        using var scope = new AssertionScope();
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.OK);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Conflict);
        current.Appointment.Status.Should().BeOneOf("Cancelled", "InProgress");
        (await Db.Scalar<int>("SELECT count(*)::int FROM resource_usage WHERE released_at IS NULL"))
            .Should().Be(current.Appointment.Status == "Cancelled" ? 0 : 2);
    }

    [Fact]
    public async Task Given_A_is_running_before_B_When_an_overrun_is_reported_Then_B_is_flagged_without_changing_its_agreement()
    {
        // Given adjacent appointments on one pair, with A in progress.
        await GivenOnlyOneAvailablePair();
        using var customer = Db.Client();
        using var staff = Db.Client("staff");
        var appointmentA = await GivenAConfirmedBooking(customer);
        var appointmentB = await GivenAConfirmedBooking(customer, new DateTimeOffset(appointmentA.Appointment.EndsAt));
        await GivenWorkHasStarted(staff, appointmentA);
        var finish = new DateTimeOffset(appointmentB.Appointment.StartsAt).AddMinutes(20);

        // When staff records a forecast that conflicts with B's accepted time.
        using var response = await staff.PatchAsJsonAsync($"/appointments/{appointmentA.Appointment.Id}/progress",
            new ProgressRequest(1, finish, "Additional inspection time"));
        var impacted = await GetBooking(customer, appointmentB.Appointment.Id);

        // Then the forecast is accepted, while B retains its promised time and price.
        using var scope = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        impacted.AtRisk.Should().BeTrue();
        impacted.Appointment.StartsAt.Should().Be(appointmentB.Appointment.StartsAt);
        impacted.Appointment.EndsAt.Should().Be(appointmentB.Appointment.EndsAt);
        impacted.Appointment.Total.Should().Be(appointmentB.Appointment.Total);
    }

    [Fact]
    public async Task Given_A_still_occupies_Bs_resources_When_B_attempts_to_start_Then_B_is_blocked()
    {
        // Given an overrun into B's reserved interval while A remains unfinished.
        var (customer, staff, appointmentA, appointmentB, finish) = await GivenAdjacentAppointmentsWithOverrun();
        using var customerScope = customer;
        using var staffScope = staff;
        Db.Clock.Set(new DateTimeOffset(appointmentB.Appointment.StartsAt));

        // When staff attempts to start B at its original time.
        using var response = await staff.PostAsJsonAsync($"/appointments/{appointmentB.Appointment.Id}/start", new ChangeRequest(1, "Start B"));

        // Then no second execution can acquire the occupied resources.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Db.Scalar<int>("SELECT count(*)::int FROM job_execution")).Should().Be(1);
    }

    [Fact]
    public async Task Given_an_overrun_leaves_too_little_time_for_B_When_A_completes_Then_B_still_requires_recovery()
    {
        // Given B cannot fit its full work duration after A's late finish.
        var (customer, staff, appointmentA, appointmentB, finish) = await GivenAdjacentAppointmentsWithOverrun();
        using var customerScope = customer;
        using var staffScope = staff;
        Db.Clock.Set(finish);

        // When staff records A's actual completion.
        using var response = await staff.PostAsJsonAsync($"/appointments/{appointmentA.Appointment.Id}/complete", new ChangeRequest(2, "Work finished"));
        var completed = await ReadSuccessful<AppointmentView>(response);
        var impacted = await GetBooking(customer, appointmentB.Appointment.Id);

        // Then occupancy is released but the insufficient remaining reservation is still at risk.
        using (new AssertionScope())
        {
            completed.Execution!.ActualCompletion.Should().Be(finish.UtcDateTime);
            (await Db.Scalar<int>("SELECT count(*)::int FROM resource_usage WHERE released_at IS NULL")).Should().Be(0);
            impacted.AtRisk.Should().BeTrue();
        }

        // When staff tries to start B in that unchanged reservation.
        using var lateStart = await staff.PostAsJsonAsync($"/appointments/{appointmentB.Appointment.Id}/start", new ChangeRequest(1, "Attempt late B"));

        // Then the application does not shorten B's work merely to make it fit.
        lateStart.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Given_an_unfinished_job_with_a_stale_forecast_When_future_capacity_is_requested_Then_its_resources_remain_unavailable()
    {
        // Given unfinished work whose original forecast has elapsed.
        await GivenOnlyOneAvailablePair();
        using var customer = Db.Client();
        using var staff = Db.Client("staff");
        var booking = await GivenAConfirmedBooking(customer);
        await GivenWorkHasStarted(staff, booking);
        Db.Clock.Set(Db.Slot.AddMinutes(80));
        var quote = await GivenAValidQuote(customer, Db.Slot.AddHours(3));

        // When another booking tries to rely on those resources being free later.
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

        // Then unknown completion is treated conservatively until staff refreshes it or finishes work.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Given_A_is_running_When_booking_and_overrun_commit_in_either_order_Then_the_conflict_is_never_hidden(bool forecastFirst)
    {
        // Given a running job and an offer for the next interval on the same resources.
        await GivenOnlyOneAvailablePair();
        using var customer = Db.Client();
        using var staff = Db.Client("staff");
        var appointmentA = await GivenAConfirmedBooking(customer);
        await GivenWorkHasStarted(staff, appointmentA);
        var quote = await GivenAValidQuote(customer, new DateTimeOffset(appointmentA.Appointment.EndsAt));
        var report = new ProgressRequest(1, new DateTimeOffset(quote.StartsAt).AddMinutes(20), "Overrun");

        // When the forecast and booking are committed in the selected order.
        HttpResponseMessage bookingResponse;
        HttpResponseMessage forecastResponse;
        if (forecastFirst)
        {
            forecastResponse = await staff.PatchAsJsonAsync($"/appointments/{appointmentA.Appointment.Id}/progress", report);
            bookingResponse = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));
        }
        else
        {
            bookingResponse = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));
            forecastResponse = await staff.PatchAsJsonAsync($"/appointments/{appointmentA.Appointment.Id}/progress", report);
        }
        using var bookingResponseScope = bookingResponse;
        using var forecastResponseScope = forecastResponse;

        // Then the forecast always persists and the booking is either rejected or explicitly at risk.
        forecastResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        if (forecastFirst)
            bookingResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
        else
        {
            var booking = await ReadSuccessful<AppointmentView>(bookingResponse);
            (await GetBooking(customer, booking.Appointment.Id)).AtRisk.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Given_a_newer_forecast_When_a_stale_report_is_submitted_Then_it_cannot_overwrite_the_forecast()
    {
        // Given a recorded forecast has advanced the execution revision.
        using var customer = Db.Client();
        using var staff = Db.Client("staff");
        var booking = await GivenAConfirmedBooking(customer);
        await GivenWorkHasStarted(staff, booking);
        var acceptedFinish = Db.Slot.AddHours(2);
        await GivenAnOverrun(staff, booking, acceptedFinish);

        // When another staff request submits a different finish using the old revision.
        using var response = await staff.PatchAsJsonAsync($"/appointments/{booking.Appointment.Id}/progress",
            new ProgressRequest(1, Db.Slot.AddHours(3), "Stale staff screen"));

        // Then the accepted forecast remains intact.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await GetBooking(customer, booking.Appointment.Id)).Execution!.ExpectedCompletion.Should().Be(acceptedFinish.UtcDateTime);
    }

    [Fact]
    public async Task Given_B_is_affected_by_As_overrun_When_B_cancels_Then_As_actual_resources_remain_occupied()
    {
        // Given B is at risk because A still uses the same resource pair.
        var (customer, staff, appointmentA, appointmentB, finish) = await GivenAdjacentAppointmentsWithOverrun();
        using var customerScope = customer;
        using var staffScope = staff;

        // When B chooses cancellation rather than accepting a delay.
        using var response = await customer.PostAsJsonAsync($"/appointments/{appointmentB.Appointment.Id}/cancel",
            new ChangeRequest(1, "Customer declines delay and cancels"));

        // Then B's impact is resolved without falsely releasing A's physical resources.
        using var scope = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Db.Scalar<int>("SELECT count(*)::int FROM resource_usage WHERE released_at IS NULL")).Should().Be(2);
        (await Db.Scalar<int>("SELECT count(*)::int FROM disruption_incident WHERE status='Open'")).Should().Be(0);
        (await GetBooking(customer, appointmentA.Appointment.Id)).Appointment.Status.Should().Be("InProgress");
    }

    private async Task<(HttpClient Customer, HttpClient Staff, AppointmentView A, AppointmentView B, DateTimeOffset Finish)> GivenAdjacentAppointmentsWithOverrun()
    {
        await GivenOnlyOneAvailablePair();
        var customer = Db.Client();
        var staff = Db.Client("staff");
        var appointmentA = await GivenAConfirmedBooking(customer);
        var appointmentB = await GivenAConfirmedBooking(customer, new DateTimeOffset(appointmentA.Appointment.EndsAt));
        await GivenWorkHasStarted(staff, appointmentA);
        var finish = new DateTimeOffset(appointmentB.Appointment.StartsAt).AddMinutes(20);
        await GivenAnOverrun(staff, appointmentA, finish);
        return (customer, staff, appointmentA, appointmentB, finish);
    }
}
