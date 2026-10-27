using System.Text.Json;
using Scheduler.Application;

namespace Scheduler.Infrastructure;

public sealed class PostgresReferenceDataReader(Database database, ReferenceDataCache cache) : IReferenceDataReader
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
        // Customer/vehicle rows remain fresh and scoped to the caller; only the public catalogue is shared.
        var customers = await s.Many<dynamic>("SELECT id,name FROM customer WHERE @staff OR id=@id", new { staff = actor.IsStaff, actor.Id });
        var vehicles = await s.Many<dynamic>("SELECT * FROM vehicle WHERE @staff OR customer_id=@id", new { staff = actor.IsStaff, actor.Id });
        var catalog = (await cache.Get<Catalog>(ReferenceCacheRegion.Catalog, "shared", async () => new(
            JsonSerializer.SerializeToElement(await s.Many<dynamic>("SELECT * FROM dealership")),
            JsonSerializer.SerializeToElement(await s.Many<dynamic>("SELECT * FROM service_type"))), ct))!;
        return new
        {
            customers, vehicles,
            dealerships = catalog.Dealerships,
            services = catalog.Services,
            paymentOptions = new[] { "PayAtCheckIn" }, bookingChannel = "Web"
        };
    }
    private sealed record Catalog(JsonElement Dealerships, JsonElement Services);
}
