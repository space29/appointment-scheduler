using System.Diagnostics;
using Npgsql;

namespace Scheduler.Api;

public sealed class BookingService(Database database, TimeProvider clock, ILogger<BookingService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<Quote> Quote(QuoteRequest request, Actor actor, CancellationToken ct)
    {
        actor.Owns(request.CustomerId, request.DealershipId);
        var now = Now;
        var start = request.StartsAt.UtcDateTime;
        if (start <= now || start > now.AddDays(365))
            throw new DomainError(400, "INVALID_TIME", "Choose a future start within 365 days.");
        await using var s = await database.Open(ct);
        var input = await s.One<QuoteInputs>("""
            SELECT st.baseline_minutes, COALESCE(dr.estimated_minutes,st.baseline_minutes) estimated_minutes,
              COALESCE(dr.buffer_minutes,0) buffer_minutes, COALESCE(dr.version,0) duration_version,
              p.id policy_id,p.base_price,p.currency,p.version price_version,d.time_zone
            FROM vehicle v JOIN service_type st ON st.id=@ServiceTypeId
            JOIN dealership d ON d.id=@DealershipId
            JOIN price_policy p ON p.dealership_id=d.id AND p.service_type_id=st.id
            LEFT JOIN duration_rule dr ON dr.service_type_id=st.id AND dr.category=v.category
            WHERE v.id=@VehicleId AND v.customer_id=@CustomerId
            """, request) ?? throw new DomainError(400, "INVALID_REFERENCE", "Check the vehicle ownership, service, and dealership.");
        var local = TimeZoneInfo.ConvertTimeFromUtc(start, TimeZoneInfo.FindSystemTimeZoneById(input.TimeZone));
        var band = await s.One<Band>("""
            SELECT name,multiplier FROM price_band WHERE policy_id=@id AND weekday=@day
            AND start_minute<=@minute AND end_minute>@minute
            """, new { id = input.PolicyId, day = (int)local.DayOfWeek, minute = local.Hour * 60 + local.Minute });
        var q = new Quote
        {
            Id = Guid.NewGuid(), CustomerId = request.CustomerId, VehicleId = request.VehicleId,
            DealershipId = request.DealershipId, ServiceTypeId = request.ServiceTypeId, StartsAt = start,
            EndsAt = start.AddMinutes(input.EstimatedMinutes + input.BufferMinutes),
            EstimatedMinutes = input.EstimatedMinutes, BufferMinutes = input.BufferMinutes, DurationVersion = input.DurationVersion,
            BasePrice = input.BasePrice, Multiplier = band?.Multiplier ?? 1, Band = band?.Name ?? "Default",
            Total = Rules.Price(input.BasePrice, band?.Multiplier ?? 1), Currency = input.Currency,
            PriceVersion = input.PriceVersion, CreatedAt = now, ExpiresAt = now.AddMinutes(10)
        };
        await s.Exec("""
            INSERT INTO appointment_quote(id,customer_id,vehicle_id,dealership_id,service_type_id,starts_at,ends_at,
            estimated_minutes,buffer_minutes,duration_version,base_price,multiplier,band,total,currency,price_version,created_at,expires_at)
            VALUES(@Id,@CustomerId,@VehicleId,@DealershipId,@ServiceTypeId,@StartsAt,@EndsAt,
            @EstimatedMinutes,@BufferMinutes,@DurationVersion,@BasePrice,@Multiplier,@Band,@Total,@Currency,@PriceVersion,@CreatedAt,@ExpiresAt)
            """, q);
        return q;
    }

    public async Task<BookingResult> Book(BookingRequest request, Actor actor, CancellationToken ct)
    {
        using var activity = Telemetry.Activities.StartActivity("booking.confirm");
        var watch = Stopwatch.StartNew();
        var outcome = "error";
        try
        {
            if (request.PaymentTiming != "PayAtCheckIn")
                throw new DomainError(400, "PAYMENT_OPTION_UNAVAILABLE", "Only payment at check-in is currently supported.");
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await using var s = await database.Open(ct);
                    await s.Begin();
                    var q = await s.One<Quote>("SELECT * FROM appointment_quote WHERE id=@id", new { id = request.QuoteId })
                        ?? throw new DomainError(404, "NOT_FOUND", "Quote not found.");
                    actor.Owns(q.CustomerId, q.DealershipId);
                    var existing = await s.One<Appointment>("SELECT * FROM appointment WHERE quote_id=@id", new { id = q.Id });
                    if (existing != null)
                    {
                        outcome = "replayed";
                        return new(await View(s, existing), false);
                    }
                    ValidateQuote(q);
                    var bays = await Candidates(s, q, "Bay");
                    var technicians = await Candidates(s, q, "Technician");
                    if (bays.Count == 0 || technicians.Count == 0)
                    {
                        // Another same-quote request may have consumed the final pair since our first read.
                        existing = await s.One<Appointment>("SELECT * FROM appointment WHERE quote_id=@id", new { id = q.Id });
                        if (existing != null) { outcome = "replayed"; return new(await View(s, existing), false); }
                        throw new DomainError(409, "NO_AVAILABILITY", "No qualified technician and bay cover the requested interval.");
                    }
                    Guid[] resources = [bays[0], technicians[0]];
                    await s.LockResources(resources);
                    q = (await s.One<Quote>("SELECT * FROM appointment_quote WHERE id=@id FOR UPDATE", new { id = q.Id }))!;
                    existing = await s.One<Appointment>("SELECT * FROM appointment WHERE quote_id=@id", new { id = q.Id });
                    if (existing != null) { outcome = "replayed"; return new(await View(s, existing), false); }
                    ValidateQuote(q);
                    if (!(await Candidates(s, q, "Bay")).Contains(resources[0]) || !(await Candidates(s, q, "Technician")).Contains(resources[1]))
                        continue; // Disposal rolls back and releases locks before selecting again.
                    var id = Guid.NewGuid();
                    var a = (await s.One<Appointment>("""
                        INSERT INTO appointment(id,quote_id,customer_id,vehicle_id,dealership_id,service_type_id,
                        starts_at,ends_at,original_starts_at,original_ends_at,estimated_minutes,buffer_minutes,duration_version,
                        price_version,total,currency,payment_timing,booking_channel,status,created_at)
                        SELECT @id,id,customer_id,vehicle_id,dealership_id,service_type_id,starts_at,ends_at,starts_at,ends_at,
                        estimated_minutes,buffer_minutes,duration_version,price_version,total,currency,'PayAtCheckIn','Web','Confirmed',@now
                        FROM appointment_quote WHERE id=@quoteId RETURNING *
                        """, new { id, quoteId = q.Id, now = Now }))!;
                    for (var i = 0; i < resources.Length; i++)
                        await s.Exec("""
                            INSERT INTO resource_reservation(id,appointment_id,resource_id,role,starts_at,ends_at)
                            VALUES(@id,@appointmentId,@resourceId,@role,@StartsAt,@EndsAt)
                            """, new { id = Guid.NewGuid(), appointmentId = id, resourceId = resources[i], role = i == 0 ? "Bay" : "LeadTechnician", q.StartsAt, q.EndsAt });
                    await s.Event(a, "AppointmentConfirmed", [a.CustomerId], Now);
                    var view = await View(s, a);
                    await s.Commit();
                    outcome = "confirmed";
                    logger.LogInformation("Booking {AppointmentId} committed on attempt {Attempt}", id, attempt);
                    return new(view, true);
                }
                catch (PostgresException ex) when (ex.SqlState is "23P01" or "40P01" or "55P03")
                {
                    logger.LogInformation("Booking contention on attempt {Attempt}: {SqlState}", attempt, ex.SqlState);
                    if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(10 * attempt), ct);
                }
            }
            throw new DomainError(409, "BOOKING_CONTENTION", "Capacity changed during booking. Retry with the same quote.");
        }
        catch (DomainError error) { outcome = error.Code; throw; }
        finally
        {
            Telemetry.Bookings.Add(1, new KeyValuePair<string, object?>("outcome", outcome));
            Telemetry.BookingLatency.Record(watch.Elapsed.TotalMilliseconds, new KeyValuePair<string, object?>("outcome", outcome));
        }
    }

    private void ValidateQuote(Quote q)
    {
        if (q.ExpiresAt <= Now) throw new DomainError(409, "QUOTE_EXPIRED", "Obtain and accept a new quote.");
        if (q.StartsAt <= Now) throw new DomainError(409, "START_IN_PAST", "The requested start is no longer in the future.");
    }

    private Task<List<Guid>> Candidates(Session s, Quote q, string kind) => s.Many<Guid>("""
        SELECT r.id FROM resource r WHERE r.dealership_id=@DealershipId AND r.kind=@kind
        AND (@kind='Bay' OR EXISTS(SELECT 1 FROM qualification k WHERE k.resource_id=r.id AND k.service_type_id=@ServiceTypeId))
        AND EXISTS(SELECT 1 FROM availability w WHERE w.resource_id=r.id AND w.starts_at<=@StartsAt AND w.ends_at>=@EndsAt)
        AND NOT EXISTS(SELECT 1 FROM resource_reservation b WHERE b.resource_id=r.id AND b.is_active
            AND tstzrange(b.starts_at,b.ends_at,'[)') && tstzrange(@StartsAt,@EndsAt,'[)'))
        AND NOT EXISTS(SELECT 1 FROM resource_usage u JOIN job_execution e ON e.id=u.execution_id
            WHERE u.resource_id=r.id AND u.released_at IS NULL
            AND (e.expected_completion<=@now OR (e.actual_start<@EndsAt AND e.expected_completion>@StartsAt)))
        ORDER BY r.id LIMIT 20
        """, new { q.DealershipId, q.ServiceTypeId, q.StartsAt, q.EndsAt, kind, now = Now });

    public async Task<AppointmentView> Get(Guid id, Actor actor, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        var a = await Find(s, id);
        actor.Owns(a.CustomerId, a.DealershipId);
        return await View(s, a);
    }

    internal static async Task<Appointment> Find(Session s, Guid id, bool locked = false) =>
        await s.One<Appointment>("SELECT * FROM appointment WHERE id=@id" + (locked ? " FOR UPDATE" : ""), new { id })
        ?? throw new DomainError(404, "NOT_FOUND", "Appointment not found.");

    internal static async Task<AppointmentView> View(Session s, Appointment a) => new(a,
        await s.Many<Reservation>("SELECT resource_id,role,starts_at,ends_at,is_active FROM resource_reservation WHERE appointment_id=@id ORDER BY role", new { a.Id }),
        await s.One<Execution>("SELECT * FROM job_execution WHERE appointment_id=@id", new { a.Id }),
        await s.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM disruption_incident WHERE affected_appointment_id=@id AND status='Open')", new { a.Id }));

    internal static async Task<Appointment> LockAppointment(Session s, Guid id, Actor actor)
    {
        var a = await Find(s, id);
        actor.Owns(a.CustomerId, a.DealershipId);
        var ids = await s.Many<Guid>("SELECT resource_id FROM resource_reservation WHERE appointment_id=@id", new { id });
        await s.LockResources(ids.ToArray());
        return await Find(s, id, true);
    }

    public async Task<AppointmentView> Cancel(Guid id, ChangeRequest request, Actor actor, CancellationToken ct)
    {
        Rules.Reason(request.Reason);
        await using var s = await database.Open(ct);
        await s.Begin();
        var a = await LockAppointment(s, id, actor);
        if (a.Status == "Cancelled") return await View(s, a);
        Rules.Revision(request.ExpectedRevision, a.Revision);
        if (a.Status != "Confirmed") throw new DomainError(409, "APPOINTMENT_STATE_CONFLICT", "Only a confirmed appointment that has not started can be cancelled.");
        a = (await s.One<Appointment>("""
            UPDATE appointment SET status='Cancelled',revision=revision+1,cancelled_at=@now,cancelled_by=@actor,cancellation_reason=@reason
            WHERE id=@id RETURNING *
            """, new { id, now = Now, actor = actor.Id, reason = request.Reason }))!;
        await s.Exec("UPDATE resource_reservation SET is_active=false,released_at=@now,release_reason='Cancellation' WHERE appointment_id=@id AND is_active", new { id, now = Now });
        await s.Exec("UPDATE disruption_incident SET status='Resolved',updated_at=@now WHERE affected_appointment_id=@id AND status='Open'", new { id, now = Now });
        await s.Event(a, "AppointmentCancelled", [a.CustomerId, DemoIds.Staff], Now);
        var view = await View(s, a);
        await s.Commit();
        return view;
    }

    private sealed class QuoteInputs
    {
        public int EstimatedMinutes { get; set; }
        public int BufferMinutes { get; set; }
        public int DurationVersion { get; set; }
        public Guid PolicyId { get; set; }
        public decimal BasePrice { get; set; }
        public string Currency { get; set; } = "";
        public int PriceVersion { get; set; }
        public string TimeZone { get; set; } = "";
    }
    private sealed record Band(string Name, decimal Multiplier);
}
