using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Scheduler.Api;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class BookingTests(DatabaseFixture db) : IAsyncLifetime
{
    public Task InitializeAsync() => db.Reset();
    public Task DisposeAsync() => Task.CompletedTask;
    private static readonly Guid VehicleA = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid VehicleB = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private async Task<Quote> Quote(HttpClient c, DateTimeOffset? start = null, bool b = false)
    {
        var response = await c.PostAsJsonAsync("/appointment-quotes", new QuoteRequest(b ? DemoIds.CustomerB : DemoIds.CustomerA,
            b ? VehicleB : VehicleA, DemoIds.Dealership, DemoIds.Service, start ?? db.Slot));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<Quote>())!;
    }
    private async Task<AppointmentView> Book(HttpClient c, Quote q)
    {
        var response = await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AppointmentView>())!;
    }
    private Task LastPair() => db.Sql("DELETE FROM availability WHERE resource_id IN ('70000000-0000-0000-0000-000000000002','80000000-0000-0000-0000-000000000002')");

    [Fact]
    public async Task QuoteAndBookingPreserveDurationPriceAndPaymentAcrossPolicyChange()
    {
        using var c = db.Client();
        var q = await Quote(c);
        Assert.Equal(60, q.EstimatedMinutes);
        Assert.Equal(10, q.BufferMinutes);
        await db.Sql("UPDATE price_policy SET base_price=999,version=2; UPDATE duration_rule SET estimated_minutes=150,version=2");
        var a = await Book(c, q);
        Assert.Equal(q.Total, a.Appointment.Total);
        Assert.Equal(60, a.Appointment.EstimatedMinutes);
        Assert.Equal("PayAtCheckIn", a.Appointment.PaymentTiming);
        Assert.Equal("Web", a.Appointment.BookingChannel);
        Assert.Equal(2, a.Resources.Count);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM outbox_message"));
        using var other = db.Client(second: true);
        var persisted = await other.GetFromJsonAsync<AppointmentView>($"/appointments/{a.Appointment.Id}");
        Assert.Equal(a.Appointment.Id, persisted!.Appointment.Id);
    }

    [Fact]
    public async Task VehicleCategoryChangesDurationAndUnknownCategoryUsesBaseline()
    {
        using var b = db.Client("customer-b");
        Assert.Equal(75, (await Quote(b, b: true)).EstimatedMinutes);
        await db.Sql("UPDATE vehicle SET category='Other' WHERE id=@id", new { id = VehicleB });
        try { Assert.Equal(60, (await Quote(b, b: true)).EstimatedMinutes); }
        finally { await db.Sql("UPDATE vehicle SET category='Large' WHERE id=@id", new { id = VehicleB }); }
    }

    [Fact]
    public async Task PricingUsesDealershipLocalWeekdayAndStartInclusiveBands()
    {
        using var c = db.Client();
        var monday = db.Baseline;
        while (monday.DayOfWeek != DayOfWeek.Monday) monday = monday.AddDays(1);
        // Baseline 01:00 UTC = 08:00 dealership local. Quoting holds no capacity.
        Assert.Equal(100m, (await Quote(c, monday.AddMinutes(59))).Total);
        Assert.Equal(120m, (await Quote(c, monday.AddHours(1))).Total);
        Assert.Equal(100m, (await Quote(c, monday.AddHours(4))).Total);
        Assert.Equal(90m, (await Quote(c, monday.AddHours(6))).Total);
    }

    [Fact]
    public async Task ConcurrentLastSlotAcrossTwoInstancesHasExactlyOneWinner()
    {
        await LastPair();
        using var c1 = db.Client(); using var c2 = db.Client(second: true);
        var q1 = await Quote(c1); var q2 = await Quote(c2);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<HttpResponseMessage> Attempt(HttpClient c, Quote q) { await gate.Task; return await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id)); }
        var t1 = Attempt(c1, q1); var t2 = Attempt(c2, q2); gate.SetResult();
        var results = await Task.WhenAll(t1, t2);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Created);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM appointment"));
        Assert.Equal(2, await db.Scalar<int>("SELECT count(*)::int FROM resource_reservation"));
    }

    [Fact]
    public async Task ConcurrentSameQuoteAndRetryAfterExpiryReturnOneAppointment()
    {
        await LastPair();
        using var c1 = db.Client(); using var c2 = db.Client(second: true);
        var q = await Quote(c1);
        var responses = await Task.WhenAll(c1.PostAsJsonAsync("/appointments", new BookingRequest(q.Id)), c2.PostAsJsonAsync("/appointments", new BookingRequest(q.Id)));
        foreach (var response in responses) Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Created);
        db.Clock.Set(db.Baseline.AddMinutes(11));
        var replay = await Book(c2, q);
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM appointment"));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM outbox_message"));
        Assert.Equal(q.Id, replay.Appointment.QuoteId);
    }

    [Fact]
    public async Task AlternativePairCanWinAfterContention()
    {
        using var c1 = db.Client(); using var c2 = db.Client(second: true);
        var q1 = await Quote(c1); var q2 = await Quote(c2);
        var results = await Task.WhenAll(Book(c1, q1), Book(c2, q2));
        Assert.Empty(results[0].Resources.Select(r => r.ResourceId).Intersect(results[1].Resources.Select(r => r.ResourceId)));
    }

    [Theory]
    [InlineData("Bay")]
    [InlineData("Technician")]
    public async Task EachResourceKindIndependentlyPreventsDoubleBooking(string kind)
    {
        await db.Sql("DELETE FROM availability WHERE resource_id IN (SELECT id FROM resource WHERE kind=@kind AND name LIKE '%2')", new { kind });
        using var c = db.Client();
        await Book(c, await Quote(c));
        var response = await c.PostAsJsonAsync("/appointments", new BookingRequest((await Quote(c)).Id));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task UnqualifiedAndPartiallyAvailableTechniciansCannotBeAllocated()
    {
        using var c = db.Client();
        await db.Sql("DELETE FROM qualification");
        var q = await Quote(c);
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id))).StatusCode);
        await db.Sql("INSERT INTO qualification SELECT id,@service FROM resource WHERE name LIKE 'Qualified%'; UPDATE availability SET ends_at=@end WHERE resource_id IN (SELECT id FROM resource WHERE kind='Technician')", new { service = DemoIds.Service, end = db.Slot.AddMinutes(30).UtcDateTime });
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id))).StatusCode);
    }

    [Fact]
    public async Task BackToBackIntervalsAreAllowedButPartialOverlapIsRejected()
    {
        await LastPair();
        using var c = db.Client();
        var q = await Quote(c); await Book(c, q);
        var overlap = await Quote(c, new DateTimeOffset(q.EndsAt).AddMinutes(-1));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/appointments", new BookingRequest(overlap.Id))).StatusCode);
        await Book(c, await Quote(c, new DateTimeOffset(q.EndsAt)));
    }

    [Fact]
    public async Task ExpiredQuoteUnsupportedPaymentAndForeignOwnershipDoNotWriteAppointments()
    {
        using var c = db.Client(); using var b = db.Client("customer-b");
        var q = await Quote(c);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id, "Online"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsJsonAsync("/appointments", new BookingRequest(q.Id))).StatusCode);
        db.Clock.Set(db.Baseline.AddMinutes(10));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id))).StatusCode);
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM appointment"));
    }

    [Fact]
    public async Task CancellationReleasesCapacityOnceAndPreservesHistory()
    {
        await LastPair();
        using var c = db.Client();
        var q = await Quote(c); var a = await Book(c, q);
        var request = new ChangeRequest(a.Appointment.Revision, "Customer changed plans");
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/cancel", request)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/cancel", request)).StatusCode);
        Assert.Equal(2, await db.Scalar<int>("SELECT count(*)::int FROM resource_reservation WHERE NOT is_active"));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM outbox_message WHERE event_type='AppointmentCancelled'"));
        await Book(c, await Quote(c));
    }

    [Fact]
    public async Task CancellationAndStartRaceHasOneConsistentWinner()
    {
        using var c = db.Client(); using var staff = db.Client("staff", true);
        var a = await Book(c, await Quote(c)); db.Clock.Set(db.Slot);
        var request = new ChangeRequest(1, "Race test");
        var responses = await Task.WhenAll(c.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/cancel", request), staff.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/start", request));
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
        var state = (await c.GetFromJsonAsync<AppointmentView>($"/appointments/{a.Appointment.Id}"))!;
        Assert.Equal(state.Appointment.Status == "Cancelled" ? 0 : 2, await db.Scalar<int>("SELECT count(*)::int FROM resource_usage WHERE released_at IS NULL"));
    }

    [Fact]
    public async Task OverrunFlagsBWithoutMovingItAndLateCompletionDoesNotAuthorizeShortenedWork()
    {
        await LastPair();
        using var c = db.Client(); using var staff = db.Client("staff");
        var a = await Book(c, await Quote(c));
        var b = await Book(c, await Quote(c, new DateTimeOffset(a.Appointment.EndsAt)));
        db.Clock.Set(db.Slot);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/start", new ChangeRequest(1, "Checked in"))).StatusCode);
        var finish = new DateTimeOffset(b.Appointment.StartsAt).AddMinutes(20);
        Assert.Equal(HttpStatusCode.OK, (await staff.PatchAsJsonAsync($"/appointments/{a.Appointment.Id}/progress", new ProgressRequest(1, finish, "Additional inspection time"))).StatusCode);
        var impacted = (await c.GetFromJsonAsync<AppointmentView>($"/appointments/{b.Appointment.Id}"))!;
        Assert.True(impacted.AtRisk);
        Assert.Equal(b.Appointment.StartsAt, impacted.Appointment.StartsAt);
        Assert.Equal(b.Appointment.Total, impacted.Appointment.Total);
        db.Clock.Set(new DateTimeOffset(b.Appointment.StartsAt));
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/appointments/{b.Appointment.Id}/start", new ChangeRequest(1, "Attempt B"))).StatusCode);
        db.Clock.Set(finish);
        Assert.Equal(HttpStatusCode.OK, (await staff.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/complete", new ChangeRequest(2, "Work finished"))).StatusCode);
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM resource_usage WHERE released_at IS NULL"));
        Assert.True((await c.GetFromJsonAsync<AppointmentView>($"/appointments/{b.Appointment.Id}"))!.AtRisk);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PostAsJsonAsync($"/appointments/{b.Appointment.Id}/start", new ChangeRequest(1, "Attempt late B"))).StatusCode);
    }

    [Fact]
    public async Task StaleForecastNeverReleasesAnUnfinishedResource()
    {
        await LastPair();
        using var c = db.Client(); using var staff = db.Client("staff");
        var a = await Book(c, await Quote(c)); db.Clock.Set(db.Slot);
        await staff.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/start", new ChangeRequest(1, "Start"));
        db.Clock.Set(db.Slot.AddMinutes(80));
        var q = await Quote(c, db.Slot.AddHours(3));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id))).StatusCode);
    }

    [Fact]
    public async Task WorkerFailureRestartAndCompetingDispatchersPreserveLogicalDelivery()
    {
        using var c = db.Client();
        var a = await Book(c, await Quote(c));
        var first = db.First.Services.GetRequiredService<NotificationDispatcher>();
        var second = db.Second.Services.GetRequiredService<NotificationDispatcher>();
        db.Provider.Fail = true;
        await first.DispatchBatch(default);
        Assert.Equal("Pending", await db.Scalar<string>("SELECT status FROM notification_delivery"));
        Assert.Equal("Confirmed", (await c.GetFromJsonAsync<AppointmentView>($"/appointments/{a.Appointment.Id}"))!.Appointment.Status);
        db.Provider.Fail = false; db.Clock.Set(db.Baseline.AddSeconds(10));
        await Task.WhenAll(first.DispatchBatch(default), second.DispatchBatch(default));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM notification_delivery"));
        Assert.Equal("ProviderAccepted", await db.Scalar<string>("SELECT status FROM notification_delivery"));
        Assert.Equal(2, await db.Scalar<int>("SELECT attempts FROM notification_delivery"));
    }

    [Fact]
    public async Task CancelledBookingSuppressesUnsentConfirmationAndInboxEnforcesOwnership()
    {
        using var c = db.Client(); using var b = db.Client("customer-b");
        var a = await Book(c, await Quote(c));
        await c.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/cancel", new ChangeRequest(1, "Cancel"));
        await db.First.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        Assert.Equal("Suppressed", await db.Scalar<string>("SELECT d.status FROM notification_delivery d JOIN outbox_message o ON o.id=d.event_id WHERE o.event_type='AppointmentConfirmed'"));
        Assert.Equal("[]", await b.GetStringAsync("/me/notifications"));
        var deliveryId = await db.Scalar<Guid>("SELECT id FROM notification_delivery WHERE recipient_id=@id LIMIT 1", new { id = DemoIds.CustomerA });
        Assert.Equal(HttpStatusCode.NotFound, (await b.PostAsync($"/me/notifications/{deliveryId}/read", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/me/notifications/{deliveryId}/read", null)).StatusCode);
    }

    [Fact]
    public async Task ApiRejectsMissingIdentityOffsetAndCustomerStaffActions()
    {
        using var anonymous = db.First.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/reference-data")).StatusCode);
        using var c = db.Client();
        var body = $$"""{"customerId":"{{DemoIds.CustomerA}}","vehicleId":"{{VehicleA}}","dealershipId":"{{DemoIds.Dealership}}","serviceTypeId":"{{DemoIds.Service}}","startsAt":"2026-12-01T10:00:00"}""";
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/appointment-quotes", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode);
        var a = await Book(c, await Quote(c));
        Assert.Equal(HttpStatusCode.Forbidden, (await c.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/start", new ChangeRequest(1, "Denied"))).StatusCode);
    }

    [Fact]
    public async Task OpenApiAndReadinessAreAvailable()
    {
        using var c = db.First.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
        Assert.Contains("/appointment-quotes", await c.GetStringAsync("/openapi/v1.json"));
    }

    [Fact]
    public async Task DatabaseConstraintRejectsOverlapsEvenWhenApplicationChecksAreBypassed()
    {
        using var c = db.Client();
        var a = await Book(c, await Quote(c));
        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Sql("""
            INSERT INTO resource_reservation(id,appointment_id,resource_id,role,starts_at,ends_at)
            SELECT gen_random_uuid(),appointment_id,resource_id,role,starts_at,ends_at FROM resource_reservation WHERE appointment_id=@id LIMIT 1
            """, new { a.Appointment.Id }));
        Assert.Equal("23P01", error.SqlState);
        Assert.Equal("reservation_no_overlap", error.ConstraintName);
        Assert.Equal(2, await db.Scalar<int>("SELECT count(*)::int FROM resource_reservation"));
    }

    [Fact]
    public async Task OutboxFailureRollsBackAppointmentAndBothReservations()
    {
        using var c = db.Client(); var q = await Quote(c);
        await db.Sql("ALTER TABLE outbox_message ADD CONSTRAINT test_injected_failure CHECK (event_type <> 'AppointmentConfirmed')");
        try
        {
            Assert.False((await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id))).IsSuccessStatusCode);
            Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM appointment"));
            Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM resource_reservation"));
            Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM outbox_message"));
        }
        finally { await db.Sql("ALTER TABLE outbox_message DROP CONSTRAINT test_injected_failure"); }
        await Book(c, q);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BookingAndForecastHaveSafeOutcomesInBothCommitOrders(bool forecastFirst)
    {
        await LastPair();
        using var c = db.Client(); using var staff = db.Client("staff");
        var a = await Book(c, await Quote(c));
        db.Clock.Set(db.Slot);
        await staff.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/start", new ChangeRequest(1, "Start A"));
        var q = await Quote(c, new DateTimeOffset(a.Appointment.EndsAt));
        var report = new ProgressRequest(1, new DateTimeOffset(q.StartsAt).AddMinutes(20), "Overrun");
        AppointmentView? b = null;
        if (!forecastFirst) b = await Book(c, q);
        Assert.Equal(HttpStatusCode.OK, (await staff.PatchAsJsonAsync($"/appointments/{a.Appointment.Id}/progress", report)).StatusCode);
        if (forecastFirst)
            Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsJsonAsync("/appointments", new BookingRequest(q.Id))).StatusCode);
        else
            Assert.True((await c.GetFromJsonAsync<AppointmentView>($"/appointments/{b!.Appointment.Id}"))!.AtRisk);
    }

    [Fact]
    public async Task StaleProgressCannotOverwriteNewerForecastAndCancellingBDoesNotReleaseA()
    {
        await LastPair();
        using var c = db.Client(); using var staff = db.Client("staff");
        var a = await Book(c, await Quote(c));
        var b = await Book(c, await Quote(c, new DateTimeOffset(a.Appointment.EndsAt)));
        db.Clock.Set(db.Slot);
        await staff.PostAsJsonAsync($"/appointments/{a.Appointment.Id}/start", new ChangeRequest(1, "Start"));
        var report = new ProgressRequest(1, new DateTimeOffset(b.Appointment.StartsAt).AddMinutes(20), "Overrun");
        await staff.PatchAsJsonAsync($"/appointments/{a.Appointment.Id}/progress", report);
        Assert.Equal(HttpStatusCode.Conflict, (await staff.PatchAsJsonAsync($"/appointments/{a.Appointment.Id}/progress", report)).StatusCode);
        await c.PostAsJsonAsync($"/appointments/{b.Appointment.Id}/cancel", new ChangeRequest(1, "Decline delay and cancel"));
        Assert.Equal(2, await db.Scalar<int>("SELECT count(*)::int FROM resource_usage WHERE released_at IS NULL"));
        Assert.Equal(0, await db.Scalar<int>("SELECT count(*)::int FROM disruption_incident WHERE status='Open'"));
    }

    [Fact]
    public async Task WorkerRecoversExpiredClaimAndFailedDeliveryCanBeRetried()
    {
        using var c = db.Client(); using var staff = db.Client("staff");
        await Book(c, await Quote(c));
        var dispatcher = db.First.Services.GetRequiredService<NotificationDispatcher>();
        db.Provider.Fail = true;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            db.Clock.Set(db.Baseline.AddMinutes(attempt));
            await dispatcher.DispatchBatch(default);
        }
        Assert.Equal("Failed", await db.Scalar<string>("SELECT status FROM notification_delivery"));
        var id = await db.Scalar<Guid>("SELECT id FROM notification_delivery");
        Assert.Equal(HttpStatusCode.Accepted, (await staff.PostAsync($"/notification-deliveries/{id}/retry", null)).StatusCode);
        // Simulate worker termination after claim commit, before provider completion.
        await db.Sql("UPDATE notification_delivery SET status='Processing',lease_token=gen_random_uuid(),lease_until=@past", new { past = db.Baseline.UtcDateTime });
        db.Provider.Fail = false;
        await db.Second.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        Assert.Equal("ProviderAccepted", await db.Scalar<string>("SELECT status FROM notification_delivery"));
        Assert.Equal(1, await db.Scalar<int>("SELECT count(*)::int FROM notification_delivery"));
    }
}
