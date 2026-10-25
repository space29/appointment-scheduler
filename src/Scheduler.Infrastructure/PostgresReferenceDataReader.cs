using Scheduler.Application;

namespace Scheduler.Infrastructure;

public sealed class PostgresReferenceDataReader(Database database) : IReferenceDataReader
{
    public async Task<bool> IsReady(CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        await s.Scalar<int>("SELECT count(*)::int FROM schema_migration");
        return true;
    }
    public async Task<object> Read(Actor actor, CancellationToken ct)
    {
        await using var s = await database.Open(ct);
        return new
        {
            customers = await s.Many<dynamic>("SELECT id,name FROM customer WHERE @staff OR id=@id", new { staff = actor.IsStaff, actor.Id }),
            vehicles = await s.Many<dynamic>("SELECT * FROM vehicle WHERE @staff OR customer_id=@id", new { staff = actor.IsStaff, actor.Id }),
            dealerships = await s.Many<dynamic>("SELECT * FROM dealership"),
            services = await s.Many<dynamic>("SELECT * FROM service_type"),
            paymentOptions = new[] { "PayAtCheckIn" }, bookingChannel = "Web"
        };
    }
}
