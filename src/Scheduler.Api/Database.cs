using System.Reflection;
using System.Text.Json;
using Dapper;
using Npgsql;

namespace Scheduler.Api;

public sealed class Database(NpgsqlDataSource source)
{
    public async Task<Session> Open(CancellationToken ct) => new(await source.OpenConnectionAsync(ct), ct);

    public async Task Migrate(bool seed, CancellationToken ct = default)
    {
        await using var s = await Open(ct);
        await s.Begin();
        // One migration runner at a time, including concurrent development hosts.
        await s.Exec("SELECT pg_advisory_xact_lock(72916540)");
        await s.Exec("CREATE TABLE IF NOT EXISTS schema_migration(name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order())
        {
            if (name.Contains("demo_seed") && !seed) continue;
            if (await s.Scalar<bool>("SELECT EXISTS(SELECT 1 FROM schema_migration WHERE name=@name)", new { name })) continue;
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            await s.Exec(await reader.ReadToEndAsync(ct));
            await s.Exec("INSERT INTO schema_migration(name) VALUES(@name)", new { name });
        }
        await s.Commit();
    }
}

public sealed class Session(NpgsqlConnection connection, CancellationToken ct) : IAsyncDisposable
{
    private NpgsqlTransaction? transaction;
    private CommandDefinition Cmd(string sql, object? args) => new(sql, args, transaction, commandTimeout: 10, cancellationToken: ct);
    public Task<int> Exec(string sql, object? args = null) => connection.ExecuteAsync(Cmd(sql, args));
    public Task<T> Scalar<T>(string sql, object? args = null) => connection.ExecuteScalarAsync<T>(Cmd(sql, args))!;
    public Task<T?> One<T>(string sql, object? args = null) => connection.QuerySingleOrDefaultAsync<T>(Cmd(sql, args));
    public async Task<List<T>> Many<T>(string sql, object? args = null) => (await connection.QueryAsync<T>(Cmd(sql, args))).AsList();
    public async Task Begin()
    {
        transaction = await connection.BeginTransactionAsync(ct);
        await Exec("SET LOCAL lock_timeout='500ms'; SET LOCAL statement_timeout='5s'");
    }
    public Task Commit() => transaction!.CommitAsync(ct);
    public async Task LockResources(Guid[] ids) => await Many<Guid>("SELECT id FROM resource WHERE id=ANY(@ids) ORDER BY id FOR UPDATE", new { ids });
    public async Task Event(Appointment a, string type, Guid[] recipients, DateTime now, object? details = null)
    {
        var payload = JsonSerializer.Serialize(new { appointmentId = a.Id, a.StartsAt, a.EndsAt, a.Total, a.Currency, a.PaymentTiming, a.Status, details });
        await Exec("""
            INSERT INTO outbox_message(id,appointment_id,event_type,revision,recipients,payload,created_at)
            VALUES(@id,@appointmentId,@type,@revision,@recipients,CAST(@payload AS jsonb),@now)
            ON CONFLICT(appointment_id,event_type,revision) DO NOTHING
            """, new { id = Guid.NewGuid(), appointmentId = a.Id, type, revision = a.Revision, recipients, payload, now });
    }
    public async ValueTask DisposeAsync()
    {
        if (transaction != null) await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
