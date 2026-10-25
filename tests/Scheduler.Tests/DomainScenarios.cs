using FluentAssertions;
using FluentAssertions.Execution;
using Scheduler.Domain;
using Xunit;

namespace Scheduler.Tests;

public sealed class DomainScenarios
{
    private static readonly DateTime Now = new(2030, 1, 7, 1, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Customer = Guid.NewGuid();
    private static BookingOffer Offer() => BookingOffer.Issue(Customer, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        Now.AddHours(2), new ServiceEstimate(60, 10, 1), new Money(100, "USD"), 1.2m, "Peak", 1, Now);
    private static ServiceAppointment Confirmed() => ServiceAppointment.Confirm(Offer(), "PayAtCheckIn", Now);

    [Fact]
    public void Given_a_valid_offer_When_confirmed_Then_the_aggregate_preserves_terms_and_raises_a_confirmation_event()
    {
        // Given an immutable, unexpired service offer.
        var offer = Offer();

        // When the customer confirms its supported payment arrangement.
        var appointment = ServiceAppointment.Confirm(offer, "PayAtCheckIn", Now);

        // Then the business agreement and the event have the same immutable snapshot.
        using var scope = new AssertionScope();
        appointment.Status.Should().Be(AppointmentStatus.Confirmed);
        appointment.AgreedPrice.Should().Be(new Money(120, "USD"));
        appointment.Reservation.Should().Be(new ServiceInterval(offer.Snapshot.StartsAt, offer.Snapshot.EndsAt));
        appointment.Events.Should().ContainSingle().Which.Should().BeOfType<AppointmentConfirmed>()
            .Which.Appointment.Should().Be(appointment.Snapshot);
    }

    [Fact]
    public void Given_an_expired_offer_When_confirmed_Then_the_domain_rejects_it_without_persistence()
    {
        // Given an offer at the exact expiry boundary.
        var offer = Offer();

        // When confirmation is attempted after its validity period.
        Action confirm = () => ServiceAppointment.Confirm(offer, "PayAtCheckIn", Now.AddMinutes(10));

        // Then the rule is enforced without an HTTP endpoint or database.
        confirm.Should().Throw<DomainError>().Which.Code.Should().Be("QUOTE_EXPIRED");
    }

    [Fact]
    public void Given_an_online_payment_request_When_confirming_Then_the_domain_rejects_the_unsupported_arrangement()
    {
        // Given a valid offer with an unsupported payment choice.
        var offer = Offer();

        // When it is confirmed using online payment.
        Action confirm = () => ServiceAppointment.Confirm(offer, "Online", Now);

        // Then the booking cannot silently substitute another arrangement.
        confirm.Should().Throw<DomainError>().Which.Code.Should().Be("PAYMENT_OPTION_UNAVAILABLE");
    }

    [Fact]
    public void Given_a_confirmed_appointment_When_cancelled_twice_Then_only_one_cancellation_event_is_raised()
    {
        // Given a confirmed appointment.
        var appointment = Confirmed();

        // When the original command is retried after cancellation.
        var changed = appointment.Cancel(1, "Changed plans", Customer, Now);
        var replayChanged = appointment.Cancel(1, "Changed plans", Customer, Now);

        // Then the transition is idempotent and preserves the original cancellation revision.
        using var scope = new AssertionScope();
        changed.Should().BeTrue();
        replayChanged.Should().BeFalse();
        appointment.Status.Should().Be(AppointmentStatus.Cancelled);
        appointment.Snapshot.Revision.Should().Be(2);
        appointment.Events.OfType<AppointmentCancelled>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(11)]
    public void Given_a_start_outside_the_reserved_work_window_When_starting_Then_the_aggregate_stays_confirmed(int offsetMinutes)
    {
        // Given a 60-minute service with ten minutes of buffer.
        var appointment = Confirmed();
        var start = appointment.Snapshot.StartsAt.AddMinutes(offsetMinutes);

        // When work would begin early or finish beyond the reserved interval.
        Action begin = () => appointment.Start(1, "Checked in", false, start);

        // Then failed validation makes no lifecycle or execution mutation.
        begin.Should().Throw<DomainError>().Which.Code.Should().Be("START_OUTSIDE_RESERVATION");
        appointment.Status.Should().Be(AppointmentStatus.Confirmed);
        appointment.Execution.Should().BeNull();
    }

    [Fact]
    public void Given_occupied_resources_When_starting_Then_the_aggregate_refuses_the_transition()
    {
        // Given the application has checked resource occupancy under the shared locks.
        var appointment = Confirmed();

        // When the requested start is blocked by that current resource state.
        Action begin = () => appointment.Start(1, "Checked in", true, appointment.Snapshot.StartsAt);

        // Then the domain does not start a second job on occupied resources.
        begin.Should().Throw<DomainError>().Which.Code.Should().Be("RESOURCE_OCCUPIED_OR_AT_RISK");
        appointment.Execution.Should().BeNull();
    }

    [Fact]
    public void Given_work_has_started_When_cancellation_is_requested_Then_the_execution_is_preserved()
    {
        // Given an in-progress appointment and its current appointment revision.
        var appointment = Confirmed();
        appointment.Start(1, "Checked in", false, appointment.Snapshot.StartsAt);
        var execution = appointment.Execution!.Snapshot;

        // When cancellation attempts to release it through the pre-start policy.
        Action cancel = () => appointment.Cancel(2, "Cancel", Customer, appointment.Snapshot.StartsAt);

        // Then the active job and its resources remain the application's responsibility.
        cancel.Should().Throw<DomainError>().Which.Code.Should().Be("APPOINTMENT_STATE_CONFLICT");
        appointment.Status.Should().Be(AppointmentStatus.InProgress);
        appointment.Execution.Snapshot.Should().Be(execution);
    }

    [Fact]
    public void Given_work_in_progress_When_the_forecast_exceeds_the_reservation_Then_the_promise_and_price_remain_unchanged()
    {
        // Given a job with accepted price and scheduled interval.
        var appointment = Confirmed();
        var promise = appointment.Reservation;
        var price = appointment.AgreedPrice;
        appointment.Start(1, "Checked in", false, promise.Start);
        var forecast = promise.End.AddMinutes(20);

        // When staff records an honest overrun.
        appointment.ReportProgress(1, forecast, "Further inspection", promise.Start);

        // Then forecast and commitment are separate facts.
        using var scope = new AssertionScope();
        appointment.Execution!.Snapshot.ExpectedCompletion.Should().Be(forecast);
        appointment.Reservation.Should().Be(promise);
        appointment.AgreedPrice.Should().Be(price);
        appointment.Events.OfType<ProgressReported>().Should().ContainSingle();
    }

    [Fact]
    public void Given_a_newer_execution_revision_When_a_stale_forecast_arrives_Then_state_and_events_do_not_change()
    {
        // Given a forecast already advanced by another staff command.
        var appointment = Confirmed();
        var start = appointment.Snapshot.StartsAt;
        appointment.Start(1, "Checked in", false, start);
        appointment.ReportProgress(1, start.AddMinutes(80), "Overrun", start);
        var before = appointment.Execution!.Snapshot;
        var eventCount = appointment.Events.Count;

        // When an old revision attempts to replace it.
        Action report = () => appointment.ReportProgress(1, start.AddMinutes(90), "Stale screen", start);

        // Then failed validation leaves both state and event history unchanged.
        report.Should().Throw<DomainError>().Which.Code.Should().Be("STALE_REVISION");
        appointment.Execution.Snapshot.Should().Be(before);
        appointment.Events.Should().HaveCount(eventCount);
    }

    [Fact]
    public void Given_an_unfinished_execution_When_its_forecast_expires_Then_it_is_stale_and_not_completed()
    {
        // Given a running appointment.
        var appointment = Confirmed();
        appointment.Start(1, "Checked in", false, appointment.Snapshot.StartsAt);
        var execution = appointment.Execution!;

        // When time reaches the forecasted end without a completion command.
        var stale = execution.HasStaleForecast(execution.Snapshot.ExpectedCompletion);

        // Then expiry is observable without fabricating actual completion.
        stale.Should().BeTrue();
        execution.Snapshot.ActualCompletion.Should().BeNull();
        appointment.Status.Should().Be(AppointmentStatus.InProgress);
    }

    [Fact]
    public void Given_an_overrunning_job_When_completed_Then_actual_time_is_saved_and_duplicate_completion_is_idempotent()
    {
        // Given a running job finishing after its promised end.
        var appointment = Confirmed();
        var originalEnd = appointment.Snapshot.EndsAt;
        appointment.Start(1, "Checked in", false, appointment.Snapshot.StartsAt);
        var finish = originalEnd.AddMinutes(20);

        // When completion is recorded and retried.
        var changed = appointment.Complete(1, "Finished", finish);
        var replayChanged = appointment.Complete(1, "Finished", finish);

        // Then actual completion stays truthful without moving the accepted reservation.
        using var scope = new AssertionScope();
        changed.Should().BeTrue();
        replayChanged.Should().BeFalse();
        appointment.Status.Should().Be(AppointmentStatus.Completed);
        appointment.Execution!.Snapshot.ActualCompletion.Should().Be(finish);
        appointment.Snapshot.EndsAt.Should().Be(originalEnd);
        appointment.Events.OfType<WorkCompleted>().Should().ContainSingle();
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void Given_a_following_appointment_When_an_overrun_completes_Then_recovery_depends_on_full_remaining_work(int minutesLate, bool needsRecovery)
    {
        // Given B has sixty minutes of work and a ten-minute buffer.
        var appointmentB = Confirmed();

        // When the preceding job frees the resources after B's scheduled start.
        var result = OverrunPolicy.RequiresRecovery(appointmentB.Snapshot,
            appointmentB.Snapshot.StartsAt.AddMinutes(minutesLate), completed: true);

        // Then the buffer absorbs only delays that still leave the full work duration.
        result.Should().Be(needsRecovery);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-1, 10)]
    [InlineData(60, -1)]
    public void Given_invalid_duration_inputs_When_creating_an_estimate_Then_the_value_object_rejects_them(int work, int buffer)
    {
        // Given invalid work or buffer duration.
        Action create = () => new ServiceEstimate(work, buffer, 1);

        // When creating the value object, then invalid estimates cannot enter the domain.
        create.Should().Throw<DomainError>().Which.Code.Should().Be("INVALID_DURATION");
    }

    [Fact]
    public void Given_an_empty_interval_When_creating_a_reservation_Then_the_value_object_rejects_it()
    {
        // Given equal start and end instants.
        Action create = () => new ServiceInterval(Now, Now);

        // When constructing the interval, then zero-duration reservations are invalid.
        create.Should().Throw<DomainError>().Which.Code.Should().Be("INVALID_INTERVAL");
    }
}
