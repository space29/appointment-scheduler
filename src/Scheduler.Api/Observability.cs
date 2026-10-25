using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Scheduler.Infrastructure;

namespace Scheduler.Api;

public static class Observability
{
    public static void AddSchedulerObservability(this WebApplicationBuilder builder)
    {
        var config = builder.Configuration.GetSection("Observability");
        // One resource identity for all three signals; instance ID distinguishes API replicas.
        var resource = ResourceBuilder.CreateDefault()
            .AddService("appointment-scheduler", serviceVersion: typeof(Observability).Assembly.GetName().Version?.ToString(),
                serviceInstanceId: Guid.NewGuid().ToString())
            .AddAttributes([new("deployment.environment.name", builder.Environment.EnvironmentName)]);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });
        builder.Logging.Configure(options => options.ActivityTrackingOptions =
            ActivityTrackingOptions.TraceId | ActivityTrackingOptions.SpanId | ActivityTrackingOptions.ParentId);
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddAttributes(resource.Build().Attributes))
            .WithLogging(logging =>
            {
                var collector = Collector(config);
                if (collector != null) logging.AddOtlpExporter(o => ConfigureExporter(o, collector, "logs"));
            }, options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
            })
            .WithTracing(tracing =>
            {
                var collector = Collector(config);
                var sampleRatio = config.GetValue("TraceSampleRatio", 1.0);
                if (!double.IsFinite(sampleRatio) || sampleRatio is < 0 or > 1)
                    throw new InvalidOperationException("Observability:TraceSampleRatio must be between 0 and 1.");
                tracing.SetSampler(new ParentBasedSampler(new TraceIdRatioBasedSampler(sampleRatio)))
                    .AddSource("Scheduler", InfrastructureTelemetry.Name)
                    .AddAspNetCoreInstrumentation(options =>
                    {
                        options.Filter = context => !context.Request.Path.StartsWithSegments("/metrics") &&
                            !context.Request.Path.StartsWithSegments("/health");
                        // No exception messages, query strings, user agents, or raw paths in exported spans.
                        options.RecordException = false;
                        options.EnrichWithHttpResponse = (activity, _) =>
                        {
                            activity.SetTag("url.query", null);
                            activity.SetTag("url.path", null);
                            activity.SetTag("user_agent.original", null);
                        };
                    });
                if (config.GetValue("ConsoleTracing", false)) tracing.AddConsoleExporter();
                if (collector != null) tracing.AddOtlpExporter(o => ConfigureExporter(o, collector, "traces"));
            })
            .WithMetrics(metrics =>
            {
                var collector = Collector(config);
                metrics.AddMeter("Scheduler", InfrastructureTelemetry.Name)
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddView("scheduler.booking.duration", new ExplicitBucketHistogramConfiguration
                    { Boundaries = [5, 10, 25, 50, 100, 250, 500, 1000, 2000, 3000, 5000, 10000] })
                    .AddPrometheusExporter();
                if (collector != null) metrics.AddOtlpExporter(o => ConfigureExporter(o, collector, "metrics"));
            });
    }

    private static Uri? Collector(IConfiguration config)
    {
        var endpoint = config["OtlpEndpoint"];
        if (string.IsNullOrWhiteSpace(endpoint)) return null;
        if (!Uri.TryCreate(endpoint.TrimEnd('/') + "/", UriKind.Absolute, out var collector) ||
            collector.Scheme is not ("http" or "https") || collector.UserInfo.Length != 0 ||
            collector.Query.Length != 0 || collector.Fragment.Length != 0)
            throw new InvalidOperationException("Observability:OtlpEndpoint must be an HTTP(S) base URL without credentials, query, or fragment.");
        return collector;
    }

    private static void ConfigureExporter(OtlpExporterOptions options, Uri collector, string signal)
    {
        options.Endpoint = new Uri(collector, "v1/" + signal);
        options.Protocol = OtlpExportProtocol.HttpProtobuf;
        options.TimeoutMilliseconds = 3000;
    }
}
