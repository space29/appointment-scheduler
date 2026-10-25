using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Dapper;
using Npgsql;
using Scheduler.Api;

namespace Scheduler.Load;

internal static class LoadProgram
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static async Task<int> Main(string[] args)
    {
        int Arg(string name, int fallback) => Array.IndexOf(args, name) is var i && i >= 0 ? int.Parse(args[i + 1]) : fallback;
        var seconds = Arg("--seconds", 60);
        var warmup = Arg("--warmup", 5);
        var reads = Arg("--reads", 1000);
        var writes = Arg("--bookings", 17);
        var replicas = Arg("--replicas", 2);
        if (seconds < 1 || warmup < 0 || reads < 0 || writes < 0 || replicas is < 1 or > 4 ||
            (long)(seconds + warmup) * writes + 64 > 365 * 120)
            throw new ArgumentException("Invalid rates/duration or insufficient seeded capacity (43,800 slots). Replicas: 1–4.");
        var admin = Environment.GetEnvironmentVariable("SCHEDULER_TEST_ADMIN")
            ?? throw new InvalidOperationException("Set SCHEDULER_TEST_ADMIN. This tool creates and removes an isolated scheduler_load_<GUID> database.");
        var root = Directory.GetCurrentDirectory();
        var api = Path.Combine(root, "src", "Scheduler.Api", "bin", "Release", "net10.0", "Scheduler.Api.dll");
        if (!File.Exists(api)) throw new InvalidOperationException("Run from the repository root after dotnet build -c Release.");
        var name = "scheduler_load_" + Guid.NewGuid().ToString("N");
        var processes = new List<Process>();
        var clients = new List<HttpClient>();
        var created = false;
        try
        {
            await using (var c = new NpgsqlConnection(admin)) { await c.OpenAsync(); await c.ExecuteAsync($"CREATE DATABASE {name}"); created = true; }
            var connection = new NpgsqlConnectionStringBuilder(admin) { Database = name, MaxPoolSize = 20, Timeout = 5 }.ConnectionString;
            DefaultTypeMap.MatchNamesWithUnderscores = true;
            await using (var source = NpgsqlDataSource.Create(connection)) await new Database(source).Migrate(true);
            await using (var c = new NpgsqlConnection(connection))
            {
                await c.ExecuteAsync("""
                    INSERT INTO resource SELECT gen_random_uuid(),'30000000-0000-0000-0000-000000000001','Bay','Load bay ' || n FROM generate_series(1,18) n;
                    INSERT INTO resource SELECT gen_random_uuid(),'30000000-0000-0000-0000-000000000001','Technician','Load technician ' || n FROM generate_series(1,18) n;
                    INSERT INTO qualification SELECT id,'40000000-0000-0000-0000-000000000001' FROM resource WHERE name LIKE 'Load technician%';
                    INSERT INTO availability
                    SELECT gen_random_uuid(),r.id,
                    ((CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date+d+time '08:00') AT TIME ZONE 'Asia/Ho_Chi_Minh',
                    ((CURRENT_TIMESTAMP AT TIME ZONE 'Asia/Ho_Chi_Minh')::date+d+time '18:00') AT TIME ZONE 'Asia/Ho_Chi_Minh'
                    FROM resource r CROSS JOIN generate_series(0,365) d WHERE r.name LIKE 'Load %';
                    """);
            }
            for (var i = 0; i < replicas; i++)
            {
                var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
                var url = $"http://127.0.0.1:{port}";
                var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(api)!, RedirectStandardOutput = true, RedirectStandardError = true };
                start.ArgumentList.Add(api);
                start.Environment["ConnectionStrings__Scheduler"] = connection;
                start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
                start.Environment["ASPNETCORE_URLS"] = url;
                start.Environment["Logging__LogLevel__Default"] = "Warning";
                var process = Process.Start(start)!;
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
                processes.Add(process);
                var client = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = 300 }) { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(10) };
                clients.Add(client);
                var ready = false;
                for (var attempt = 0; attempt < 100; attempt++)
                {
                    if (process.HasExited) throw new InvalidOperationException("Load API process exited before readiness.");
                    try { if ((await client.GetAsync("/health/ready")).IsSuccessStatusCode) { ready = true; break; } }
                    catch (HttpRequestException) { }
                    await Task.Delay(100);
                }
                if (!ready) throw new InvalidOperationException("Load API failed readiness.");
            }
            var baseDay = DateTimeOffset.UtcNow.Date.AddDays(1).AddHours(1); // 08:00 dealership local
            long sequence = -1;
            var samples = new List<(Guid Id, string Actor)>();
            var allLatencies = new ConcurrentBag<double>();
            var successLatencies = new ConcurrentBag<double>();
            long readOk = 0, readFail = 0, booked = 0, bookingFail = 0, requestCount = 0, dropped = 0;
            var failures = new ConcurrentDictionary<string, long>();
            long lateIterations = 0;
            void Failure(string code) => failures.AddOrUpdate(code, 1, (_, count) => count + 1);
            async Task<(Guid, string)?> Book(bool measure)
            {
                var index = Interlocked.Increment(ref sequence);
                var actor = index % 2 == 0 ? "customer-a" : "customer-b";
                var startTime = new DateTimeOffset(DateTime.SpecifyKind(baseDay.AddDays(index / 120).AddMinutes(index % 6 * 90), DateTimeKind.Utc));
                var client = clients[(int)(index % clients.Count)];
                try
                {
                    using var quoteRequest = Request(HttpMethod.Post, "/appointment-quotes", actor,
                        new QuoteRequest(actor == "customer-a" ? DemoIds.CustomerA : DemoIds.CustomerB,
                            Guid.Parse(actor == "customer-a" ? "20000000-0000-0000-0000-000000000001" : "20000000-0000-0000-0000-000000000002"), DemoIds.Dealership, DemoIds.Service, startTime));
                    if (measure) Interlocked.Increment(ref requestCount);
                    using var qr = await client.SendAsync(quoteRequest);
                    if (!qr.IsSuccessStatusCode) { if (measure) { Interlocked.Increment(ref bookingFail); Failure("quote_" + (int)qr.StatusCode); } return null; }
                    var quote = (await qr.Content.ReadFromJsonAsync<Quote>())!;
                    using var bookingRequest = Request(HttpMethod.Post, "/appointments", actor, new BookingRequest(quote.Id));
                    var watch = Stopwatch.StartNew();
                    if (measure) Interlocked.Increment(ref requestCount);
                    using var response = await client.SendAsync(bookingRequest);
                    var latency = watch.Elapsed.TotalMilliseconds;
                    if (measure) allLatencies.Add(latency);
                    if (response.StatusCode != HttpStatusCode.Created)
                    {
                        if (measure)
                        {
                            Interlocked.Increment(ref bookingFail);
                            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
                            Failure("booking_" + (problem.TryGetProperty("title", out var title) ? title.GetString() : ((int)response.StatusCode).ToString()));
                        }
                        return null;
                    }
                    var appointment = (await response.Content.ReadFromJsonAsync<AppointmentView>())!;
                    if (measure) { Interlocked.Increment(ref booked); successLatencies.Add(latency); }
                    return (appointment.Appointment.Id, actor);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
                { if (measure) { Interlocked.Increment(ref bookingFail); Failure("booking_" + e.GetType().Name); } return null; }
            }
            for (var i = 0; i < 64; i++) samples.Add(await Book(false) ?? throw new InvalidOperationException("Failed to prepare read fixtures."));
            async Task Read(long i, bool measure)
            {
                var sample = samples[(int)(i % samples.Count)];
                var path = i % 10 < 5 ? $"/appointments/{sample.Id}" : i % 10 < 8 ? "/me/notifications?limit=20" : "/reference-data";
                try
                {
                    using var request = Request(HttpMethod.Get, path, sample.Actor);
                    if (measure) Interlocked.Increment(ref requestCount);
                    using var response = await clients[(int)(i % clients.Count)].SendAsync(request);
                    if (measure) { if (response.IsSuccessStatusCode) Interlocked.Increment(ref readOk); else { Interlocked.Increment(ref readFail); Failure("read_" + (int)response.StatusCode); } }
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { if (measure) { Interlocked.Increment(ref readFail); Failure("read_" + e.GetType().Name); } }
            }
            async Task Schedule(int rate, int duration, Func<long, Task> action, bool measure)
            {
                if (rate == 0 || duration == 0) return;
                var watch = Stopwatch.StartNew();
                var pending = new List<Task>();
                for (long i = 0; i < (long)rate * duration; i++)
                {
                    var wait = TimeSpan.FromSeconds((double)i / rate) - watch.Elapsed;
                    // Missed arrivals are dropped, not replayed as an artificial catch-up burst.
                    // This also exposes host suspension or a starved generator as an invalid capacity run.
                    if (wait.TotalMilliseconds < -100)
                    {
                        if (measure) { Interlocked.Increment(ref dropped); Interlocked.Increment(ref lateIterations); }
                        continue;
                    }
                    if (wait > TimeSpan.Zero) await Task.Delay(wait);
                    pending.RemoveAll(t => t.IsCompletedSuccessfully);
                    if (pending.Count >= 2000) { if (measure) Interlocked.Increment(ref dropped); continue; }
                    pending.Add(action(i));
                }
                await Task.WhenAll(pending);
            }
            Console.WriteLine($"Warm-up {warmup}s, then {seconds}s at {reads} reads/s + {writes} bookings/s, {replicas} API processes.");
            await Task.WhenAll(Schedule(reads, warmup, i => Read(i, false), false), Schedule(writes, warmup, async _ => { await Book(false); }, false));
            var measurement = Stopwatch.StartNew();
            await Task.WhenAll(Schedule(reads, seconds, i => Read(i, true), true), Schedule(writes, seconds, async _ => { await Book(true); }, true));
            var elapsed = Math.Max(seconds, measurement.Elapsed.TotalSeconds);
            await using var validation = new NpgsqlConnection(connection);
            var overlaps = await validation.ExecuteScalarAsync<int>("""
                SELECT count(*)::int FROM resource_reservation a JOIN resource_reservation b
                ON a.id<b.id AND a.resource_id=b.resource_id AND a.is_active AND b.is_active
                AND tstzrange(a.starts_at,a.ends_at,'[)') && tstzrange(b.starts_at,b.ends_at,'[)')
                """);
            var backlog = await validation.ExecuteScalarAsync<int>("SELECT (SELECT count(*) FROM outbox_message WHERE processed_at IS NULL)+(SELECT count(*) FROM notification_delivery WHERE status IN ('Pending','Processing'))");
            var result = new
            {
                measuredAtUtc = DateTimeOffset.UtcNow, runtime = Environment.Version.ToString(), logicalProcessors = Environment.ProcessorCount,
                replicas, durationSeconds = seconds, elapsedSecondsIncludingDrain = elapsed, warmupSeconds = warmup,
                requestedReadRate = reads, requestedBookingRate = writes, successfulReads = readOk, failedReads = readFail,
                successfulNewBookings = booked, failedBookingWorkflows = bookingFail, httpRequests = requestCount, droppedIterations = dropped,
                lateGeneratorIterations = lateIterations, failureReasons = failures,
                achievedReadsPerSecond = readOk / elapsed, achievedBookingsPerSecond = booked / elapsed,
                confirmationP50Ms = Percentile(successLatencies, .50), confirmationP95Ms = Percentile(successLatencies, .95), confirmationP99Ms = Percentile(successLatencies, .99),
                allBookingResponsesP99Ms = Percentile(allLatencies, .99), overlappingReservations = overlaps, notificationBacklogAtEnd = backlog,
                note = "Local synthetic benchmark with co-located generator, APIs and PostgreSQL. Round-robin clients, no load balancer. Not evidence of production HA or the full workload."
            };
            Directory.CreateDirectory(Path.Combine(root, "artifacts"));
            var output = Path.Combine(root, "artifacts", $"load-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
            await File.WriteAllTextAsync(output, JsonSerializer.Serialize(result, Json));
            Console.WriteLine(JsonSerializer.Serialize(result, Json));
            Console.WriteLine($"Results: {output}");
            return readFail == 0 && bookingFail == 0 && dropped == 0 && overlaps == 0 && elapsed <= seconds + 1 &&
                readOk >= (long)reads * seconds && booked >= (long)writes * seconds &&
                (writes == 0 || (Percentile(successLatencies, .95) <= 2000 && Percentile(successLatencies, .99) <= 3000)) ? 0 : 1;
        }
        finally
        {
            foreach (var client in clients) client.Dispose();
            foreach (var process in processes) { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); }
            NpgsqlConnection.ClearAllPools();
            if (created) { await using var c = new NpgsqlConnection(admin); await c.OpenAsync(); await c.ExecuteAsync($"DROP DATABASE IF EXISTS {name} WITH (FORCE)"); }
        }
    }
    private static HttpRequestMessage Request(HttpMethod method, string path, string actor, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("X-Demo-Actor", actor);
        if (body != null) request.Content = JsonContent.Create(body);
        return request;
    }
    private static double Percentile(ConcurrentBag<double> values, double fraction)
    {
        var sorted = values.Order().ToArray();
        return sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * fraction) - 1, 0, sorted.Length - 1)];
    }
}
