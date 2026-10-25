using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Scheduler.Application;
using Scheduler.Infrastructure;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class ObservabilityScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_a_traced_booking_When_another_worker_delivers_Then_logs_spans_and_metrics_are_exported_and_correlated()
    {
        // Given a real local OTLP/HTTP receiver and a caller-provided distributed trace.
        await using var collector = await LocalCollector.Start();
        var spans = new SpanExporter();
        var logs = new LogExporter();
        await using var api = ObservedApi(spans, logs, collector.Address);
        using var customer = api.CreateClient();
        customer.DefaultRequestHeaders.Add("X-Demo-Actor", "customer-a");
        var quote = await GivenAValidQuote(customer);
        var traceId = ActivityTraceId.CreateRandom().ToString();
        customer.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-0123456789abcdef-01");

        // When the API commits and a separate worker instance consumes the persisted event.
        using var response = await customer.PostAsJsonAsync("/appointments?private=do-not-export", new BookingRequest(quote.Id));
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        await Db.Second.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        await api.Services.GetRequiredService<OperationalMetrics>().Refresh(default);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush().Should().BeTrue();
        // The Prometheus pull reader cannot be force-flushed; assert the OTLP push at the receiver below.
        api.Services.GetRequiredService<MeterProvider>().ForceFlush();
        api.Services.GetRequiredService<LoggerProvider>().ForceFlush().Should().BeTrue();

        // Then request, database, and asynchronous delivery share the same trace; delivery parents the saved booking span.
        response.Headers.GetValues("X-Trace-Id").Should().ContainSingle().Which.Should().Be(traceId);
        var trace = spans.Items.Where(a => a.TraceId.ToString() == traceId).ToArray();
        var booking = trace.Should().ContainSingle(a => a.DisplayName == "booking.confirm").Subject;
        var delivery = trace.Should().ContainSingle(a => a.DisplayName == "notification.deliver").Subject;
        trace.Should().Contain(a => a.Kind == ActivityKind.Server);
        trace.Should().Contain(a => a.DisplayName == "postgresql.commit" && a.ParentSpanId == booking.SpanId);
        delivery.ParentSpanId.Should().Be(booking.SpanId);
        delivery.GetTagItem("notification.outcome").Should().Be("ProviderAccepted");
        (await Db.Scalar<string>("SELECT trace_parent FROM outbox_message WHERE event_type='AppointmentConfirmed'"))
            .Should().Be(booking.Id);
        logs.Items.Should().Contain(log => log.TraceId == traceId && log.SpanId == booking.SpanId.ToString() && log.Message.Contains("committed"));
        trace.SelectMany(a => a.Tags).Should().NotContain(tag => tag.Key == "db.statement" || tag.Key == "db.query.text" || tag.Key == "url.query");
        string.Join(" ", trace.SelectMany(a => a.Tags).Select(t => t.Value)).Should().NotContain("do-not-export");

        // Then all three actual OTLP protobuf requests reached the collector, not just an in-process listener.
        collector.Requests.Should().Contain(r => r.Signal == "traces" && Encoding.UTF8.GetString(r.Body).Contains("booking.confirm"));
        collector.Requests.Should().Contain(r => r.Signal == "metrics" && Encoding.UTF8.GetString(r.Body).Contains("scheduler.booking.outcomes"));
        collector.Requests.Should().Contain(r => r.Signal == "logs" && Encoding.UTF8.GetString(r.Body).Contains("committed"));
        collector.Requests.Should().OnlyContain(r => r.ContentType == "application/x-protobuf" && r.Body.Length > 0);
        customer.DefaultRequestHeaders.Remove("X-Demo-Actor");
        var metrics = await customer.GetStringAsync("/metrics");
        metrics.Should().Contain("scheduler_booking_outcomes_total").And.Contain("scheduler_booking_duration_milliseconds_bucket")
            .And.Contain("scheduler_database_duration_milliseconds").And.Contain("scheduler_outbox_pending");
        metrics.Should().NotContain(quote.Id.ToString()).And.NotContain(traceId);
    }

    [Fact]
    public async Task Given_a_failed_provider_When_delivery_is_retried_Then_failure_and_recovery_remain_in_the_booking_trace()
    {
        // Given a booking and a provider that rejects the first delivery attempt.
        var spans = new SpanExporter();
        await using var api = ObservedApi(spans, new LogExporter());
        using var customer = api.CreateClient();
        customer.DefaultRequestHeaders.Add("X-Demo-Actor", "customer-a");
        var booking = await GivenAConfirmedBooking(customer);
        var worker = api.Services.GetRequiredService<NotificationDispatcher>();
        Db.Provider.Fail = true;
        await worker.DispatchBatch(default);

        // When the due retry succeeds after recovery.
        Db.Provider.Fail = false;
        Db.Clock.Set(Db.Baseline.AddMinutes(1));
        await worker.DispatchBatch(default);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();

        // Then both attempts retain the persisted parent and only the failed attempt is marked Error.
        var savedParent = await Db.Scalar<string>("SELECT trace_parent FROM outbox_message WHERE appointment_id=@id", new { id = booking.Appointment.Id });
        ActivityContext.TryParse(savedParent, null, out var parent).Should().BeTrue();
        var attempts = spans.Items.Where(a => a.DisplayName == "notification.deliver" && a.TraceId == parent.TraceId).ToArray();
        attempts.Should().HaveCount(2).And.OnlyContain(a => a.ParentSpanId == parent.SpanId);
        attempts[0].Status.Should().Be(ActivityStatusCode.Error);
        attempts[0].GetTagItem("notification.outcome").Should().Be("Pending");
        attempts[1].Status.Should().NotBe(ActivityStatusCode.Error);
        attempts[1].GetTagItem("notification.outcome").Should().Be("ProviderAccepted");
        var outcomes = MetricLines(await customer.GetStringAsync("/metrics"), "scheduler_notification_outcomes_total");
        outcomes.Should().Contain(l => l.Contains("outcome=\"Pending\""))
            .And.Contain(l => l.Contains("outcome=\"ProviderAccepted\""));
    }

    [Fact]
    public async Task Given_an_overdue_unfinished_job_When_metrics_are_collected_Then_stale_work_and_snapshot_freshness_are_visible()
    {
        // Given work whose forecast expired without a completion report.
        await using var api = ObservedApi(new SpanExporter(), new LogExporter());
        using var customer = api.CreateClient();
        customer.DefaultRequestHeaders.Add("X-Demo-Actor", "customer-a");
        var booking = await GivenAConfirmedBooking(customer);
        using var staff = api.CreateClient();
        staff.DefaultRequestHeaders.Add("X-Demo-Actor", "staff");
        await GivenWorkHasStarted(staff, booking);
        Db.Clock.Set(new DateTimeOffset(booking.Appointment.EndsAt).AddMinutes(1));

        // When the database snapshot is refreshed and then ages without another refresh.
        await api.Services.GetRequiredService<OperationalMetrics>().Refresh(default);
        Db.Clock.Set(Db.Clock.GetUtcNow().AddSeconds(30));

        // Then a scrape exposes the stale job and the age of its evidence, without querying the database itself.
        var metrics = await customer.GetStringAsync("/metrics");
        MetricValues(metrics, "scheduler_execution_stale").Should().Contain(1);
        MetricValues(metrics, "scheduler_operations_snapshot_age_seconds").Should().Contain(30);
        MetricValues(metrics, "scheduler_outbox_pending").Should().Contain(1);
        MetricValues(metrics, "scheduler_incident_open").Should().Contain(0);
    }

    [Fact]
    public async Task Given_a_legacy_event_without_trace_context_When_it_is_delivered_Then_delivery_still_succeeds_with_a_new_trace()
    {
        // Given a durable event written before trace metadata existed.
        var spans = new SpanExporter();
        await using var api = ObservedApi(spans, new LogExporter());
        using var customer = api.CreateClient();
        customer.DefaultRequestHeaders.Add("X-Demo-Actor", "customer-a");
        await GivenAConfirmedBooking(customer);
        await Db.Sql("UPDATE outbox_message SET trace_parent=NULL,trace_state=NULL");

        // When a worker consumes it.
        await api.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();

        // Then adding telemetry has not made old events undeliverable.
        (await Db.Scalar<string>("SELECT status FROM notification_delivery")).Should().Be("ProviderAccepted");
        spans.Items.Should().Contain(a => a.DisplayName == "notification.deliver" && a.TraceId != default);
    }

    [Fact]
    public async Task Given_an_unavailable_collector_When_a_booking_is_confirmed_Then_telemetry_failure_cannot_undo_the_commit()
    {
        // Given an OTLP receiver that returns 503 for every signal.
        await using var collector = await LocalCollector.Start(available: false);
        await using var api = ObservedApi(new SpanExporter(), new LogExporter(), collector.Address);
        using var customer = api.CreateClient();
        customer.DefaultRequestHeaders.Add("X-Demo-Actor", "customer-a");

        // When booking commits and queued traces encounter the unavailable receiver.
        var booking = await GivenAConfirmedBooking(customer);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();

        // Then booking is durable and the independent pull metrics still work.
        collector.Requests.Should().Contain(r => r.Signal == "traces");
        (await GetBooking(customer, booking.Appointment.Id)).Appointment.Status.Should().Be("Confirmed");
        MetricLines(await customer.GetStringAsync("/metrics"), "scheduler_booking_outcomes_total")
            .Should().Contain(l => l.Contains("outcome=\"confirmed\""));
    }

    [Fact]
    public async Task Given_a_caller_that_disables_trace_sampling_When_booking_and_delivery_run_Then_metrics_still_count_the_work()
    {
        // Given an incoming W3C trace with its recorded flag disabled.
        var spans = new SpanExporter();
        await using var api = ObservedApi(spans, new LogExporter());
        using var customer = api.CreateClient();
        customer.DefaultRequestHeaders.Add("X-Demo-Actor", "customer-a");
        var traceId = ActivityTraceId.CreateRandom().ToString();
        customer.DefaultRequestHeaders.Add("traceparent", $"00-{traceId}-0123456789abcdef-00");

        // When booking and asynchronous notification delivery succeed.
        await GivenAConfirmedBooking(customer);
        await api.Services.GetRequiredService<NotificationDispatcher>().DispatchBatch(default);
        api.Services.GetRequiredService<TracerProvider>().ForceFlush();

        // Then sampling suppresses spans without suppressing business counters or durable trace propagation.
        spans.Items.Should().NotContain(a => a.TraceId.ToString() == traceId);
        var parent = await Db.Scalar<string>("SELECT trace_parent FROM outbox_message");
        parent.Should().Contain(traceId).And.EndWith("-00");
        var metrics = await customer.GetStringAsync("/metrics");
        MetricLines(metrics, "scheduler_booking_outcomes_total").Should().Contain(l => l.Contains("outcome=\"confirmed\""));
        MetricLines(metrics, "scheduler_notification_outcomes_total").Should().Contain(l => l.Contains("outcome=\"ProviderAccepted\""));
    }

    private static string[] MetricLines(string metrics, string name) => metrics.Split('\n')
        .Where(line => line.StartsWith(name + "{") || line.StartsWith(name + " ")).ToArray();
    private static double[] MetricValues(string metrics, string name) => MetricLines(metrics, name)
        .Select(line => double.Parse(Regex.Match(line, @"(?:\}|\w) ([\d.]+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> ObservedApi(SpanExporter spans, LogExporter logs, string? collector = null) =>
        Db.First.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Observability:OtlpEndpoint", collector ?? "");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Observability:OtlpEndpoint"] = collector ?? "",
                ["Logging:LogLevel:Default"] = "Information"
            }));
            builder.ConfigureServices(services => services.AddOpenTelemetry()
                .WithTracing(t => t.AddProcessor(new SimpleActivityExportProcessor(spans)))
                .WithLogging(l => l.AddProcessor(new SimpleLogRecordExportProcessor(logs))));
        });

    private sealed class SpanExporter : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Items { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch) Items.Enqueue(activity);
            return ExportResult.Success;
        }
    }
    private sealed record LogEvidence(string TraceId, string SpanId, string Message);
    private sealed class LogExporter : BaseExporter<LogRecord>
    {
        public ConcurrentQueue<LogEvidence> Items { get; } = new();
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var log in batch) Items.Enqueue(new(log.TraceId.ToString(), log.SpanId.ToString(), log.FormattedMessage ?? ""));
            return ExportResult.Success;
        }
    }
    private sealed record OtlpRequest(string Signal, string? ContentType, byte[] Body);
    private sealed class LocalCollector(WebApplication app, string address, ConcurrentQueue<OtlpRequest> requests) : IAsyncDisposable
    {
        public string Address { get; } = address;
        public ConcurrentQueue<OtlpRequest> Requests { get; } = requests;
        public static async Task<LocalCollector> Start(bool available = true)
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            var app = builder.Build();
            var requests = new ConcurrentQueue<OtlpRequest>();
            app.MapPost("/v1/{signal}", async (HttpContext context, string signal) =>
            {
                using var bytes = new MemoryStream();
                await context.Request.Body.CopyToAsync(bytes);
                requests.Enqueue(new(signal, context.Request.ContentType, bytes.ToArray()));
                context.Response.ContentType = "application/x-protobuf";
                context.Response.StatusCode = available ? 200 : 503; // Empty protobuf Export*ServiceResponse is valid on success.
            });
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            return new(app, address, requests);
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
