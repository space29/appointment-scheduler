using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Scheduler.Domain;

namespace Scheduler.Application;

public sealed class BookingService(ISchedulingStore store, TimeProvider clock, IStaffDirectory staff, ILogger<BookingService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Quote> Quote(QuoteRequest request, Actor actor, CancellationToken ct)
    {
        actor.Owns(request.CustomerId, request.DealershipId);
        await using var s = await store.Open(ct);
        var input = await s.QuoteInputs(request)
            ?? throw new RequestFailure(FailureType.Validation, "INVALID_REFERENCE", "Check the vehicle ownership, service, and dealership.");
        var start = request.StartsAt.UtcDateTime;
        var local = TimeZoneInfo.ConvertTimeFromUtc(start, TimeZoneInfo.FindSystemTimeZoneById(input.TimeZone));
        var band = await s.PriceBand(input.PolicyId, (int)local.DayOfWeek, local.Hour * 60 + local.Minute);
        var offer = BookingOffer.Issue(request.CustomerId, request.VehicleId, request.DealershipId, request.ServiceTypeId,
            start, new ServiceEstimate(input.EstimatedMinutes, input.BufferMinutes, input.DurationVersion),
            new Money(input.BasePrice, input.Currency), band?.Multiplier ?? 1, band?.Name ?? "Default", input.PriceVersion, Now);
        await s.AddQuote(offer.Snapshot);
        return offer.Snapshot.ToDto();
    }

    public async Task<BookingResult> Book(BookingRequest request, Actor actor, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("booking.confirm");
        var watch = Stopwatch.StartNew();
        var outcome = "error";
        try
        {
            BookingOffer.EnsurePaymentSupported(request.PaymentTiming);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await using var s = await store.Open(ct);
                    await s.Begin();
                    var quote = await s.Quote(request.QuoteId)
                        ?? throw new RequestFailure(FailureType.NotFound, "NOT_FOUND", "Quote not found.");
                    actor.Owns(quote.CustomerId, quote.DealershipId);
                    var existing = await s.ByQuote(quote.Id);
                    if (existing != null) { outcome = "replayed"; return new(await s.View(existing), false); }
                    BookingOffer.Restore(quote).EnsureAcceptable(Now);
                    var bays = await s.Candidates(quote, "Bay", Now);
                    var technicians = await s.Candidates(quote, "Technician", Now);
                    if (bays.Count == 0 || technicians.Count == 0)
                    {
                        existing = await s.ByQuote(quote.Id);
                        if (existing != null) { outcome = "replayed"; return new(await s.View(existing), false); }
                        throw new RequestFailure(FailureType.Conflict, "NO_AVAILABILITY", "No qualified technician and bay cover the requested interval.");
                    }
                    Guid[] resources = [bays[0], technicians[0]];
                    await s.LockResources(resources);
                    quote = (await s.Quote(quote.Id, forUpdate: true))!;
                    existing = await s.ByQuote(quote.Id);
                    if (existing != null) { outcome = "replayed"; return new(await s.View(existing), false); }
                    var offer = BookingOffer.Restore(quote);
                    offer.EnsureAcceptable(Now);
                    if (!(await s.Candidates(quote, "Bay", Now)).Contains(resources[0]) ||
                        !(await s.Candidates(quote, "Technician", Now)).Contains(resources[1])) continue;

                    var appointment = ServiceAppointment.Confirm(offer, request.PaymentTiming, Now);
                    await s.AddBooking(appointment, resources[0], resources[1]);
                    await AppointmentWorkflow.Events(s, appointment, staff);
                    var view = await s.View(appointment);
                    await s.Commit();
                    outcome = "confirmed";
                    logger.LogInformation("Booking {AppointmentId} committed on attempt {Attempt}", appointment.Snapshot.Id, attempt);
                    return new(view, true);
                }
                catch (ContentionFailure ex)
                {
                    logger.LogInformation("Booking contention on attempt {Attempt}: {Code}", attempt, ex.Code);
                    if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), ct);
                }
            }
            throw new RequestFailure(FailureType.Conflict, "BOOKING_CONTENTION", "Capacity changed during booking. Retry with the same quote.");
        }
        catch (DomainError error) { outcome = error.Code; throw; }
        catch (RequestFailure error) { outcome = error.Code; throw; }
        finally
        {
            Telemetry.Bookings.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
            Telemetry.BookingLatency.Record(watch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    public async Task<AppointmentView> Get(Guid id, Actor actor, CancellationToken ct)
    {
        await using var s = await store.Open(ct);
        var appointment = await AppointmentWorkflow.Find(s, id);
        actor.Owns(appointment.Snapshot.CustomerId, appointment.Snapshot.DealershipId);
        return await s.View(appointment);
    }

    public async Task<AppointmentView> Cancel(Guid id, ChangeRequest request, Actor actor, CancellationToken ct)
    {
        await using var s = await store.Open(ct);
        await s.Begin();
        var appointment = await AppointmentWorkflow.Lock(s, id, actor);
        var now = Now;
        if (!appointment.Cancel(request.ExpectedRevision, request.Reason, actor.Id, now)) return await s.View(appointment);
        await s.Save(appointment);
        await s.ReleaseReservations(id, now);
        await s.ResolveCancelledImpacts(id, now);
        await AppointmentWorkflow.Events(s, appointment, staff);
        var view = await s.View(appointment);
        await s.Commit();
        return view;
    }
}
