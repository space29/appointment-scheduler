using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Scheduler.Application;
using Scheduler.Infrastructure;
using Xunit;

namespace Scheduler.Tests;

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>;

public sealed class TestClock : TimeProvider
{
    private long ticks = DateTimeOffset.UtcNow.UtcTicks;
    public void Set(DateTimeOffset now) => Interlocked.Exchange(ref ticks, now.UtcTicks);
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
}
public sealed class TestProvider : IDeliveryProvider
{
    public bool Fail { get; set; }
    public Task Send(Guid id, CancellationToken ct)
    {
        if (Fail) throw new DeliveryUnavailableException();
        return Task.CompletedTask;
    }
}

public sealed class ApiFactory(string connection, TestClock clock, TestProvider provider) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Scheduler"] = connection,
            ["Notifications:Enabled"] = "false",
            ["Logging:LogLevel:Default"] = "Warning"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(clock);
            services.RemoveAll<IDeliveryProvider>();
            services.AddSingleton<IDeliveryProvider>(provider);
        });
    }
}

public sealed class DatabaseFixture : IAsyncLifetime
{
    private readonly string name = "scheduler_test_" + Guid.NewGuid().ToString("N");
    private string admin = "";
    public string Connection { get; private set; } = "";
    public TestClock Clock { get; } = new();
    public TestProvider Provider { get; } = new();
    public ApiFactory First { get; private set; } = null!;
    public ApiFactory Second { get; private set; } = null!;
    public DateTimeOffset Baseline { get; } = new(DateTime.UtcNow.Date.AddDays(1).AddHours(1), TimeSpan.Zero);
    public DateTimeOffset Slot => Baseline.AddHours(2);

    public async Task InitializeAsync()
    {
        admin = Environment.GetEnvironmentVariable("SCHEDULER_TEST_ADMIN")
            ?? throw new InvalidOperationException("Set SCHEDULER_TEST_ADMIN to a PostgreSQL connection with CREATEDB. Tests create and remove only their own scheduler_test_<random> database.");
        await using (var c = new NpgsqlConnection(admin))
        {
            await c.OpenAsync();
            await c.ExecuteAsync($"CREATE DATABASE {name}"); // name is generated exclusively from a fixed prefix and GUID.
        }
        Connection = new NpgsqlConnectionStringBuilder(admin) { Database = name, MaxPoolSize = 20, IncludeErrorDetail = false }.ConnectionString;
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        await using (var source = NpgsqlDataSource.Create(Connection))
            await new Database(source).Migrate(true);
        First = new(Connection, Clock, Provider);
        Second = new(Connection, Clock, Provider);
    }

    public async Task Reset()
    {
        Clock.Set(Baseline);
        Provider.Fail = false;
        await Sql("""
            TRUNCATE notification_delivery,outbox_message,execution_report,disruption_incident,resource_usage,
            job_execution,resource_reservation,appointment,appointment_quote;
            UPDATE price_policy SET base_price=100,version=1;
            UPDATE duration_rule SET estimated_minutes=CASE category WHEN 'Large' THEN 75 ELSE 60 END,buffer_minutes=10,version=1;
            UPDATE vehicle SET category='Large' WHERE id='20000000-0000-0000-0000-000000000002';
            DELETE FROM qualification;
            INSERT INTO qualification SELECT id,'40000000-0000-0000-0000-000000000001' FROM resource WHERE name LIKE 'Qualified%';
            DELETE FROM availability;
            INSERT INTO availability SELECT gen_random_uuid(),id,@start,@end FROM resource;
            """, new { start = Baseline.UtcDateTime, end = Baseline.AddHours(10).UtcDateTime });
    }

    public HttpClient Client(string actor = "customer-a", bool second = false)
    {
        var client = (second ? Second : First).CreateClient();
        client.DefaultRequestHeaders.Add("X-Demo-Actor", actor);
        return client;
    }
    public async Task Sql(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(Connection);
        await c.ExecuteAsync(sql, args);
    }
    public async Task<T> Scalar<T>(string sql, object? args = null)
    {
        await using var c = new NpgsqlConnection(Connection);
        return (await c.ExecuteScalarAsync<T>(sql, args))!;
    }
    public async Task DisposeAsync()
    {
        if (First != null) await First.DisposeAsync();
        if (Second != null) await Second.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        if (string.IsNullOrEmpty(admin)) return;
        await using var c = new NpgsqlConnection(admin);
        await c.OpenAsync();
        await c.ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)");
    }
}
