using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.DependencyInjection;
using Scheduler.Api;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class NotificationScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_a_confirmed_booking_When_the_provider_fails_Then_delivery_is_pending_and_the_booking_remains_confirmed()
    {
        // Given a durable confirmation event and an unavailable provider.
        using var customer = Db.Client();
        var booking = await GivenAConfirmedBooking(customer);
        Db.Provider.Fail = true;
        var worker = Db.First.Services.GetRequiredService<NotificationDispatcher>();

        // When the worker attempts delivery.
        await worker.DispatchBatch(default);

        // Then failure is retryable and cannot roll back a committed appointment.
        using var scope = new AssertionScope();
        (await Db.Scalar<string>("SELECT status FROM notification_delivery")).Should().Be("Pending");
        (await Db.Scalar<string>("SELECT last_error FROM notification_delivery")).Should().Be("PROVIDER_UNAVAILABLE");
        (await GetBooking(customer, booking.Appointment.Id)).Appointment.Status.Should().Be("Confirmed");
    }

    [Fact]
    public async Task Given_a_pending_retry_When_two_workers_resume_after_recovery_Then_one_logical_delivery_is_accepted()
    {
        // Given a failed first attempt whose retry is now due.
        using var customer = Db.Client();
        await GivenAConfirmedBooking(customer);
        var firstWorker = Db.First.Services.GetRequiredService<NotificationDispatcher>();
        var secondWorker = Db.Second.Services.GetRequiredService<NotificationDispatcher>();
        Db.Provider.Fail = true;
        await firstWorker.DispatchBatch(default);
        var originalDeliveryId = await Db.Scalar<Guid>("SELECT id FROM notification_delivery");
        Db.Provider.Fail = false;
        Db.Clock.Set(Db.Baseline.AddSeconds(10));

        // When both worker instances compete to resume delivery.
        await Task.WhenAll(firstWorker.DispatchBatch(default), secondWorker.DispatchBatch(default));

        // Then the stable delivery identity is reused and only one retry is recorded.
        using var scope = new AssertionScope();
        (await Db.Scalar<int>("SELECT count(*)::int FROM notification_delivery")).Should().Be(1);
        (await Db.Scalar<Guid>("SELECT id FROM notification_delivery")).Should().Be(originalDeliveryId);
        (await Db.Scalar<string>("SELECT status FROM notification_delivery")).Should().Be("ProviderAccepted");
        (await Db.Scalar<int>("SELECT attempts FROM notification_delivery")).Should().Be(2);
    }

    [Fact]
    public async Task Given_delivery_has_exhausted_its_retries_When_staff_retries_after_recovery_Then_the_existing_delivery_can_succeed()
    {
        // Given five provider failures have left a visible failed delivery.
        using var customer = Db.Client();
        using var staff = Db.Client("staff");
        await GivenAConfirmedBooking(customer);
        var worker = Db.First.Services.GetRequiredService<NotificationDispatcher>();
        Db.Provider.Fail = true;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Db.Clock.Set(Db.Baseline.AddMinutes(attempt));
            await worker.DispatchBatch(default);
        }
        (await Db.Scalar<string>("SELECT status FROM notification_delivery")).Should().Be("Failed", "the scenario requires exhausted automatic retries");
        var deliveryId = await Db.Scalar<Guid>("SELECT id FROM notification_delivery");
        Db.Provider.Fail = false;

        // When staff requests another attempt and a worker processes it.
        using var response = await staff.PostAsync($"/notification-deliveries/{deliveryId}/retry", null);
        await Db.Second.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);

        // Then the delivery succeeds without inventing a new logical message.
        using var scope = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Db.Scalar<Guid>("SELECT id FROM notification_delivery")).Should().Be(deliveryId);
        (await Db.Scalar<string>("SELECT status FROM notification_delivery")).Should().Be("ProviderAccepted");
        (await Db.Scalar<int>("SELECT count(*)::int FROM notification_delivery")).Should().Be(1);
    }

    [Fact]
    public async Task Given_a_worker_abandoned_an_expired_claim_When_another_worker_runs_Then_the_delivery_is_recovered()
    {
        // Given a durable delivery, claimed by a worker that disappeared before acknowledgement.
        using var customer = Db.Client();
        await GivenAConfirmedBooking(customer);
        Db.Provider.Fail = true;
        await Db.First.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        var deliveryId = await Db.Scalar<Guid>("SELECT id FROM notification_delivery");
        await Db.Sql("UPDATE notification_delivery SET status='Processing',lease_token=gen_random_uuid(),lease_until=@past",
            new { past = Db.Baseline.AddSeconds(-1).UtcDateTime });
        Db.Provider.Fail = false;

        // When another instance processes the expired claim.
        await Db.Second.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);

        // Then durable work is recovered with its original identity.
        using var scope = new AssertionScope();
        (await Db.Scalar<Guid>("SELECT id FROM notification_delivery")).Should().Be(deliveryId);
        (await Db.Scalar<string>("SELECT status FROM notification_delivery")).Should().Be("ProviderAccepted");
        (await Db.Scalar<int>("SELECT count(*)::int FROM notification_delivery")).Should().Be(1);
    }

    [Fact]
    public async Task Given_cancellation_precedes_confirmation_delivery_When_the_worker_runs_Then_the_obsolete_confirmation_is_suppressed()
    {
        // Given a cancelled booking whose confirmation has not been sent.
        using var customer = Db.Client();
        var booking = await GivenAConfirmedBooking(customer);
        await GivenACancelledBooking(customer, booking);

        // When the worker processes the committed events.
        await Db.First.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);

        // Then the obsolete confirmation is suppressed while cancellation can be delivered.
        using var scope = new AssertionScope();
        (await Db.Scalar<string>("SELECT d.status FROM notification_delivery d JOIN outbox_message o ON o.id=d.event_id WHERE o.event_type='AppointmentConfirmed'"))
            .Should().Be("Suppressed");
        (await Db.Scalar<int>("SELECT count(*)::int FROM notification_delivery d JOIN outbox_message o ON o.id=d.event_id WHERE o.event_type='AppointmentCancelled' AND d.status='ProviderAccepted'"))
            .Should().Be(2, "both the customer and staff receive cancellation updates");
    }

    [Fact]
    public async Task Given_a_customers_notification_When_another_customer_reads_or_marks_it_Then_access_is_denied()
    {
        // Given a confirmation notification belonging to customer A.
        using var owner = Db.Client();
        using var otherCustomer = Db.Client("customer-b");
        await GivenAConfirmedBooking(owner);
        await Db.First.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        var deliveryId = await Db.Scalar<Guid>("SELECT id FROM notification_delivery WHERE recipient_id=@id", new { id = DemoIds.CustomerA });

        // When customer B views their inbox and attempts to mark A's notification as read.
        var otherInbox = await otherCustomer.GetFromJsonAsync<List<NotificationDispatcher.InboxItem>>("/me/notifications");
        using var denied = await otherCustomer.PostAsync($"/me/notifications/{deliveryId}/read", null);

        // Then A's notification is neither disclosed nor marked as read.
        using var scope = new AssertionScope();
        otherInbox.Should().BeEmpty();
        denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Db.Scalar<DateTime?>("SELECT read_at FROM notification_delivery WHERE id=@id", new { id = deliveryId })).Should().BeNull();
    }

    [Fact]
    public async Task Given_an_unread_notification_When_its_recipient_marks_it_read_Then_read_status_is_recorded()
    {
        // Given an unread confirmation for the authenticated customer.
        using var customer = Db.Client();
        var booking = await GivenAConfirmedBooking(customer);
        await Db.First.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        var deliveryId = await Db.Scalar<Guid>("SELECT id FROM notification_delivery WHERE recipient_id=@id", new { id = DemoIds.CustomerA });

        // When its recipient marks the notification as read.
        using var response = await customer.PostAsync($"/me/notifications/{deliveryId}/read", null);

        // Then only notification read state changes; the accepted appointment is preserved.
        using var scope = new AssertionScope();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Db.Scalar<DateTime?>("SELECT read_at FROM notification_delivery WHERE id=@id", new { id = deliveryId })).Should().Be(Db.Clock.GetUtcNow().UtcDateTime);
        (await GetBooking(customer, booking.Appointment.Id)).Appointment.Should().BeEquivalentTo(booking.Appointment);
    }
}
