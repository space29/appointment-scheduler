using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Scheduler.Application;

public static class Telemetry
{
    public static readonly ActivitySource Activities = new("Scheduler");
    public static readonly Meter Meter = new("Scheduler");
    public static readonly Counter<long> Bookings = Meter.CreateCounter<long>("scheduler.booking.outcomes");
    public static readonly Histogram<double> BookingLatency = Meter.CreateHistogram<double>("scheduler.booking.duration", "ms");
    public static readonly Counter<long> Deliveries = Meter.CreateCounter<long>("scheduler.notification.outcomes");
}
