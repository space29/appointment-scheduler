using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Scheduler.Infrastructure;

public static class InfrastructureTelemetry
{
    public const string Name = "Scheduler.Infrastructure";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("scheduler.database.duration", "ms");

    internal static async Task<T> Observe<T>(string operation, Func<Task<T>> action)
    {
        using var activity = Activities.StartActivity("postgresql." + operation, ActivityKind.Client);
        activity?.SetTag("db.system.name", "postgresql");
        activity?.SetTag("db.operation.name", operation);
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        try { return await action(); }
        catch (Exception ex)
        {
            outcome = ex is OperationCanceledException ? "cancelled" : "error";
            activity?.SetStatus(ActivityStatusCode.Error);
            activity?.SetTag("error.type", ex.GetType().Name);
            throw;
        }
        finally
        {
            Duration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new("operation", operation), new("outcome", outcome));
        }
    }
}
