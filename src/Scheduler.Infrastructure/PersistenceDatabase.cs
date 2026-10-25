using System.Reflection;
using Dapper;
using Npgsql;
using Scheduler.Application;

namespace Scheduler.Infrastructure;

public sealed class Database(NpgsqlDataSource source)
{
    internal async Task<Session> Open(CancellationToken ct)
    {
        try { return new(await source.OpenConnectionAsync(ct), ct); }
        catch (NpgsqlException ex) { throw Translate(ex); }
    }
    internal static PersistenceFailure Translate(NpgsqlException ex) => ex is PostgresException p && p.SqlState is "23P01" or "40P01" or "55P03"
        ? new ContentionFailure(p.SqlState, ex) : new PersistenceFailure((ex as PostgresException)?.SqlState ?? "CONNECTION_FAILURE", ex);

    public async Task Migrate(bool seed, CancellationToken ct = default)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using var s = await Open(ct);
        await s.Begin();
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

internal sealed class Session(NpgsqlConnection connection, CancellationToken ct) : IAsyncDisposable
{
    private NpgsqlTransaction? transaction;
    private CommandDefinition Cmd(string sql, object? args) => new(sql, args, transaction, commandTimeout: 10, cancellationToken: ct);
    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try { return await action(); }
        catch (NpgsqlException ex) { throw Database.Translate(ex); }
    }
    public Task<int> Exec(string sql, object? args = null) => Run(() => connection.ExecuteAsync(Cmd(sql, args)));
    public Task<T> Scalar<T>(string sql, object? args = null) => Run(async () => (await connection.ExecuteScalarAsync<T>(Cmd(sql, args)))!);
    public Task<T?> One<T>(string sql, object? args = null) => Run(() => connection.QuerySingleOrDefaultAsync<T>(Cmd(sql, args)));
    public Task<List<T>> Many<T>(string sql, object? args = null) => Run(async () => (await connection.QueryAsync<T>(Cmd(sql, args))).AsList());
    public async Task Begin()
    {
        try { transaction = await connection.BeginTransactionAsync(ct); }
        catch (NpgsqlException ex) { throw Database.Translate(ex); }
        await Exec("SET LOCAL lock_timeout='500ms'; SET LOCAL statement_timeout='5s'");
    }
    public async Task Commit()
    {
        try { await transaction!.CommitAsync(ct); }
        catch (NpgsqlException ex) { throw Database.Translate(ex); }
    }
    public async Task LockResources(Guid[] ids) => await Many<Guid>("SELECT id FROM resource WHERE id=ANY(@ids) ORDER BY id FOR UPDATE", new { ids });
    public async ValueTask DisposeAsync()
    {
        if (transaction != null) await transaction.DisposeAsync();
        await connection.DisposeAsync();
    }
}
