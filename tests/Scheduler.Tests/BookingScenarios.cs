using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Npgsql;
using Scheduler.Application;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class BookingScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_a_confirmed_booking_When_read_from_another_instance_Then_the_persisted_booking_is_returned()
    {
        // Given a booking committed through the first API instance.
        using var customer = Db.Client();
        var original = await GivenAConfirmedBooking(customer);
        using var otherInstance = Db.Client(second: true);

        // When the customer retrieves it through another instance.
        var persisted = await GetBooking(otherInstance, original.Appointment.Id);

        // Then both instances observe the same committed agreement and allocation.
        persisted.Should().BeEquivalentTo(original);
    }

    [Fact]
    public async Task Given_one_remaining_pair_When_two_instances_book_simultaneously_Then_exactly_one_booking_is_confirmed()
    {
        // Given independent quotes competing for the final qualified resource pair.
        await GivenOnlyOneAvailablePair();
        using var firstCustomer = Db.Client();
        using var secondCustomer = Db.Client(second: true);
        var firstQuote = await GivenAValidQuote(firstCustomer);
        var secondQuote = await GivenAValidQuote(secondCustomer);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Submit(HttpClient customer, Quote quote)
        {
            await gate.Task;
            return await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));
        }

        // When both requests are released together through separate API instances.
        var firstAttempt = Submit(firstCustomer, firstQuote);
        var secondAttempt = Submit(secondCustomer, secondQuote);
        gate.SetResult();
        var responses = await Task.WhenAll(firstAttempt, secondAttempt);

        // Then one customer wins and no partial or duplicate allocation persists.
        using var scope = new AssertionScope();
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Created);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Conflict);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(1);
        (await Db.Scalar<int>("SELECT count(*)::int FROM resource_reservation")).Should().Be(2);
    }

    [Fact]
    public async Task Given_the_same_quote_When_confirmed_concurrently_Then_both_responses_identify_one_booking()
    {
        // Given one offer and two API instances.
        await GivenOnlyOneAvailablePair();
        using var firstCustomer = Db.Client();
        using var secondCustomer = Db.Client(second: true);
        var quote = await GivenAValidQuote(firstCustomer);

        // When both instances confirm that offer concurrently.
        var responses = await Task.WhenAll(
            firstCustomer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id)),
            secondCustomer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id)));
        var bookings = await Task.WhenAll(responses.Select(ReadSuccessful<AppointmentView>));

        // Then one response creates the booking and the other replays that same result.
        using var scope = new AssertionScope();
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.Created);
        responses.Should().ContainSingle(r => r.StatusCode == HttpStatusCode.OK);
        bookings.Select(b => b.Appointment.Id).Distinct().Should().ContainSingle();
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(1);
        (await Db.Scalar<int>("SELECT count(*)::int FROM outbox_message")).Should().Be(1);
    }

    [Fact]
    public async Task Given_a_consumed_quote_that_has_expired_When_retried_Then_the_original_booking_is_returned()
    {
        // Given a confirmed booking whose original offer is now expired.
        using var customer = Db.Client();
        var original = await GivenAConfirmedBooking(customer);
        Db.Clock.Set(Db.Baseline.AddMinutes(11));
        using var otherInstance = Db.Client(second: true);

        // When a client reconciles a lost response using the original quote ID.
        using var response = await otherInstance.PostAsJsonAsync("/appointments", new BookingRequest(original.Appointment.QuoteId));
        var replay = await ReadSuccessful<AppointmentView>(response);

        // Then expiry does not turn a completed booking into a second booking or a failure.
        using var scope = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Appointment.Id.Should().Be(original.Appointment.Id);
        replay.Appointment.QuoteId.Should().Be(original.Appointment.QuoteId);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(1);
        (await Db.Scalar<int>("SELECT count(*)::int FROM outbox_message")).Should().Be(1);
    }

    [Fact]
    public async Task Given_two_available_pairs_When_bookings_compete_Then_both_receive_distinct_resources()
    {
        // Given two offers and enough physical capacity for both.
        using var firstCustomer = Db.Client();
        using var secondCustomer = Db.Client(second: true);
        var firstQuote = await GivenAValidQuote(firstCustomer);
        var secondQuote = await GivenAValidQuote(secondCustomer);

        // When the offers are confirmed concurrently.
        var responses = await Task.WhenAll(
            firstCustomer.PostAsJsonAsync("/appointments", new BookingRequest(firstQuote.Id)),
            secondCustomer.PostAsJsonAsync("/appointments", new BookingRequest(secondQuote.Id)));
        var bookings = await Task.WhenAll(responses.Select(ReadSuccessful<AppointmentView>));

        // Then re-selection uses the alternative pair without overlapping either resource.
        using var scope = new AssertionScope();
        responses.Should().OnlyContain(r => r.StatusCode == HttpStatusCode.Created);
        bookings.SelectMany(b => b.Resources).Select(r => r.ResourceId).Should().OnlyHaveUniqueItems();
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(2);
    }

    [Theory]
    [InlineData("Bay")]
    [InlineData("Technician")]
    public async Task Given_a_fully_booked_resource_kind_When_another_booking_requests_that_time_Then_it_is_rejected(string kind)
    {
        // Given only one resource of this kind, already reserved for the requested interval.
        await Db.Sql("DELETE FROM availability WHERE resource_id IN (SELECT id FROM resource WHERE kind=@kind AND name LIKE '%2')", new { kind });
        using var customer = Db.Client();
        await GivenAConfirmedBooking(customer);
        var quote = await GivenAValidQuote(customer);

        // When another booking tries to use that interval.
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

        // Then availability of the other resource kind cannot authorize double booking.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(1);
    }

    [Fact]
    public async Task Given_only_unqualified_technicians_When_confirming_Then_no_booking_is_created()
    {
        // Given free technicians who lack the requested service qualification.
        using var customer = Db.Client();
        await Db.Sql("DELETE FROM qualification");
        var quote = await GivenAValidQuote(customer);

        // When the customer confirms the quote.
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

        // Then being free is insufficient to qualify for the service.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(0);
    }

    [Fact]
    public async Task Given_technicians_available_for_only_part_of_the_service_When_confirming_Then_no_booking_is_created()
    {
        // Given qualified technicians whose shifts end before the service would finish.
        using var customer = Db.Client();
        await Db.Sql("UPDATE availability SET ends_at=@end WHERE resource_id IN (SELECT id FROM resource WHERE kind='Technician')",
            new { end = Db.Slot.AddMinutes(30).UtcDateTime });
        var quote = await GivenAValidQuote(customer);

        // When the customer confirms a service longer than the available interval.
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

        // Then the full service cannot be allocated.
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(0);
    }

    [Theory]
    [InlineData(-1, HttpStatusCode.Conflict, 1)]
    [InlineData(0, HttpStatusCode.Created, 2)]
    public async Task Given_an_existing_reservation_When_booking_at_its_end_boundary_Then_only_nonoverlapping_time_is_accepted(
        int offsetMinutes, HttpStatusCode expectedStatus, int expectedBookings)
    {
        // Given one pair reserved until a known end instant.
        await GivenOnlyOneAvailablePair();
        using var customer = Db.Client();
        var original = await GivenAConfirmedBooking(customer);
        var quote = await GivenAValidQuote(customer, new DateTimeOffset(original.Appointment.EndsAt).AddMinutes(offsetMinutes));

        // When a new booking starts adjacent to, or just inside, that reservation.
        using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

        // Then half-open intervals permit adjacency while preventing overlap.
        response.StatusCode.Should().Be(expectedStatus);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(expectedBookings);
    }

    [Theory]
    [InlineData("expired", HttpStatusCode.Conflict)]
    [InlineData("online-payment", HttpStatusCode.BadRequest)]
    [InlineData("foreign-owner", HttpStatusCode.NotFound)]
    public async Task Given_an_invalid_confirmation_When_submitted_Then_no_appointment_is_written(string scenario, HttpStatusCode expectedStatus)
    {
        // Given an offer that is expired, requests unsupported payment, or belongs to another customer.
        using var owner = Db.Client();
        using var submitter = Db.Client(scenario == "foreign-owner" ? "customer-b" : "customer-a");
        var quote = await GivenAValidQuote(owner);
        if (scenario == "expired") Db.Clock.Set(Db.Baseline.AddMinutes(10));
        var request = new BookingRequest(quote.Id, scenario == "online-payment" ? "Online" : "PayAtCheckIn");

        // When that confirmation is submitted.
        using var response = await submitter.PostAsJsonAsync("/appointments", request);

        // Then the business rejection creates neither an appointment nor a notification event.
        using var scope = new AssertionScope();
        response.StatusCode.Should().Be(expectedStatus);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(0);
        (await Db.Scalar<int>("SELECT count(*)::int FROM outbox_message")).Should().Be(0);
    }

    [Fact]
    public async Task Given_a_reserved_resource_When_an_overlapping_insert_bypasses_the_API_Then_PostgreSQL_rejects_it()
    {
        // Given an already committed reservation.
        using var customer = Db.Client();
        var booking = await GivenAConfirmedBooking(customer);

        // When a writer attempts to bypass application checks and duplicate the interval.
        Func<Task> insertOverlap = () => Db.Sql("""
            INSERT INTO resource_reservation(id,appointment_id,resource_id,role,starts_at,ends_at)
            SELECT gen_random_uuid(),appointment_id,resource_id,role,starts_at,ends_at
            FROM resource_reservation WHERE appointment_id=@id LIMIT 1
            """, new { booking.Appointment.Id });

        // Then the shared database constraint enforces the booking invariant.
        var failure = await insertOverlap.Should().ThrowAsync<PostgresException>();
        using var scope = new AssertionScope();
        failure.Which.SqlState.Should().Be("23P01");
        failure.Which.ConstraintName.Should().Be("reservation_no_overlap");
        (await Db.Scalar<int>("SELECT count(*)::int FROM resource_reservation")).Should().Be(2);
    }

    [Fact]
    public async Task Given_notification_persistence_will_fail_When_confirming_Then_the_entire_booking_rolls_back()
    {
        // Given an otherwise valid offer and an injected failure at the final outbox insert.
        using var customer = Db.Client();
        var quote = await GivenAValidQuote(customer);
        await Db.Sql("ALTER TABLE outbox_message ADD CONSTRAINT test_injected_failure CHECK (event_type <> 'AppointmentConfirmed')");
        try
        {
            // When the booking attempts to commit its confirmation event.
            using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

            // Then no appointment, reservation, or event is partially committed.
            using var scope = new AssertionScope();
            response.IsSuccessStatusCode.Should().BeFalse();
            (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(0);
            (await Db.Scalar<int>("SELECT count(*)::int FROM resource_reservation")).Should().Be(0);
            (await Db.Scalar<int>("SELECT count(*)::int FROM outbox_message")).Should().Be(0);
        }
        finally
        {
            await Db.Sql("ALTER TABLE outbox_message DROP CONSTRAINT test_injected_failure");
        }

        // When the infrastructure fault is removed, the original quote remains usable.
        using var retry = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

        // Then retry can create the appointment that was previously rolled back.
        retry.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
