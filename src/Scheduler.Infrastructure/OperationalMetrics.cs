using System.Diagnostics.Metrics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Scheduler.Infrastructure;

// Scrapes read an in-memory snapshot, never execute database queries on the metrics callback thread.
public sealed class OperationalMetrics : BackgroundService
{
    private readonly Database database;
    private readonly TimeProvider clock;
    private readonly IConfiguration config;
    private readonly ILogger<OperationalMetrics> logger;
    private readonly Meter meter = new(InfrastructureTelemetry.Name);
    private Snapshot? snapshot;

    public OperationalMetrics(Database database, TimeProvider clock, IConfiguration config, ILogger<OperationalMetrics> logger)
    {
        this.database = database;
        this.clock = clock;
        this.config = config;
        this.logger = logger;
        Gauge("scheduler.outbox.pending", s => s.OutboxPending);
        Gauge("scheduler.outbox.oldest.age", s => s.OldestCreatedAt is { } oldest
            ? Math.Max(0, (clock.GetUtcNow().UtcDateTime - oldest).TotalSeconds) : 0, "s");
        Gauge("scheduler.notification.pending", s => s.DeliveriesPending);
        Gauge("scheduler.notification.failed", s => s.DeliveriesFailed);
        Gauge("scheduler.execution.stale", s => s.StaleForecasts);
        Gauge("scheduler.incident.open", s => s.OpenIncidents);
        Gauge("scheduler.operations.snapshot.age", s => Math.Max(0, (clock.GetUtcNow().UtcDateTime - s.ObservedAt).TotalSeconds), "s");
    }

    private void Gauge(string name, Func<Snapshot, double> value, string? unit = null) =>
        meter.CreateObservableGauge(name, () => Volatile.Read(ref snapshot) is { } current
            ? new[] { new Measurement<double>(value(current)) } : [], unit);

    public async Task Refresh(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var session = await database.Open(ct);
        var current = await session.One<Snapshot>("""
            SELECT
              (SELECT count(*) FROM outbox_message WHERE processed_at IS NULL) outbox_pending,
              (SELECT min(created_at) FROM outbox_message WHERE processed_at IS NULL) oldest_created_at,
              (SELECT count(*) FROM notification_delivery WHERE status IN ('Pending','Processing')) deliveries_pending,
              (SELECT count(*) FROM notification_delivery WHERE status='Failed') deliveries_failed,
              (SELECT count(*) FROM job_execution WHERE actual_completion IS NULL AND expected_completion<=@now) stale_forecasts,
              (SELECT count(*) FROM disruption_incident WHERE status='Open') open_incidents,
              @now::timestamptz observed_at
            """, new { now });
        Volatile.Write(ref snapshot, current);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Observability:OperationalPolling", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Refresh(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogWarning("Operational metrics refresh failed with {ErrorType}; previous snapshot retained", ex.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Observability:PollSeconds", 5), 1, 300)), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    public override void Dispose() { meter.Dispose(); base.Dispose(); }

    private sealed class Snapshot
    {
        public long OutboxPending { get; set; }
        public DateTime? OldestCreatedAt { get; set; }
        public long DeliveriesPending { get; set; }
        public long DeliveriesFailed { get; set; }
        public long StaleForecasts { get; set; }
        public long OpenIncidents { get; set; }
        public DateTime ObservedAt { get; set; }
    }
}
