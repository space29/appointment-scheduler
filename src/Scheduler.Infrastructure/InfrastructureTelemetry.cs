using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Scheduler.Infrastructure;

public static class InfrastructureTelemetry
{
    public const string Name = "Scheduler.Infrastructure";
    public static readonly ActivitySource Activities = new(Name);
    private static readonly Meter Meter = new(Name);
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("scheduler.database.duration", "ms");
    private static readonly Counter<long> CacheRequests = Meter.CreateCounter<long>("scheduler.cache.requests");
    private static readonly Counter<long> CacheLoads = Meter.CreateCounter<long>("scheduler.cache.loads");

    internal static void CacheRequest(string region, string outcome) => CacheRequests.Add(1, new("cache", region), new("outcome", outcome));
    internal static void CacheLoad(string region, string outcome) => CacheLoads.Add(1, new("cache", region), new("outcome", outcome));

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
