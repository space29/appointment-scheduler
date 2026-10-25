using System.Diagnostics;
using Dapper;
using Npgsql;
using Scheduler.Api;

DefaultTypeMap.MatchNamesWithUnderscores = true;
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new ExplicitOffsetConverter()));
builder.Services.AddProblemDetails();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp =>
{
    var connection = sp.GetRequiredService<IConfiguration>().GetConnectionString("Scheduler")
        ?? throw new InvalidOperationException("Set ConnectionStrings__Scheduler or a .NET user secret. Never commit credentials.");
    return NpgsqlDataSource.Create(connection);
});
builder.Services.AddSingleton<Database>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<OperationsService>();
builder.Services.AddSingleton<IDeliveryProvider, MockDeliveryProvider>();
builder.Services.AddSingleton<NotificationDispatcher>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(sp => (Actor)sp.GetRequiredService<IHttpContextAccessor>().HttpContext!.Items["Actor"]!);
var app = builder.Build();

if (args.Contains("--migrate"))
{
    await app.Services.GetRequiredService<Database>().Migrate(args.Contains("--seed"));
    Console.WriteLine("Database migrations completed.");
    return;
}
// Mock authentication is an explicit assessment boundary. Fail closed outside local/test execution.
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("This assessment uses demo identity headers. Configure production authentication before deploying outside Development/Testing.");

app.Use(async (context, next) =>
{
    var started = Stopwatch.GetTimestamp();
    try { await next(context); }
    catch (DomainError error)
    {
        await Results.Problem(statusCode: error.Status, title: error.Code, detail: error.Message,
            extensions: new Dictionary<string, object?> { ["code"] = error.Code, ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
    }
    catch (BadHttpRequestException)
    {
        await Results.Problem(statusCode: 400, title: "INVALID_REQUEST", detail: "Check the JSON body, identifiers, and explicit timestamp offsets.").ExecuteAsync(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
    catch (Exception ex)
    {
        // SQL details may contain business data. Log a bounded classification, never connection strings or bodies.
        app.Logger.LogError("Request {TraceId} failed: {ErrorType}, database state {SqlState}", context.TraceIdentifier, ex.GetType().Name, (ex as PostgresException)?.SqlState);
        var unavailable = ex is NpgsqlException or TimeoutException;
        await Results.Problem(statusCode: unavailable ? 503 : 500, title: unavailable ? "SERVICE_UNAVAILABLE" : "UNEXPECTED_ERROR",
            detail: "The request could not be completed. For booking, retry with the same quote identifier.",
            extensions: new Dictionary<string, object?> { ["traceId"] = context.TraceIdentifier }).ExecuteAsync(context);
    }
    finally
    {
        app.Logger.LogInformation("HTTP {Method} {Route} returned {Status} in {ElapsedMs} ms, trace {TraceId}",
            context.Request.Method, (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched",
            context.Response.StatusCode, Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.TraceIdentifier);
    }
});

app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/openapi"))
    { await next(context); return; }
    var value = context.Request.Headers["X-Demo-Actor"].ToString();
    Actor? actor = value switch
    {
        "customer-a" => new(DemoIds.CustomerA, false),
        "customer-b" => new(DemoIds.CustomerB, false),
        "staff" => new(DemoIds.Staff, true),
        _ => null
    };
    if (actor == null) throw new DomainError(401, "DEMO_IDENTITY_REQUIRED", "Set X-Demo-Actor to customer-a, customer-b, or staff for the local demonstration.");
    context.Items["Actor"] = actor;
    await next(context);
});

app.MapOpenApi();
app.MapGet("/health/live", () => Results.Ok(new { status = "Alive" }));
app.MapGet("/health/ready", async (Database db, CancellationToken ct) =>
{
    await using var s = await db.Open(ct);
    await s.Scalar<int>("SELECT count(*)::int FROM schema_migration");
    return Results.Ok(new { status = "Ready" });
});
app.MapGet("/reference-data", async (Database db, Actor actor, CancellationToken ct) =>
{
    await using var s = await db.Open(ct);
    return Results.Ok(new
    {
        customers = await s.Many<dynamic>("SELECT id,name FROM customer WHERE @staff OR id=@id", new { staff = actor.IsStaff, actor.Id }),
        vehicles = await s.Many<dynamic>("SELECT * FROM vehicle WHERE @staff OR customer_id=@id", new { staff = actor.IsStaff, actor.Id }),
        dealerships = await s.Many<dynamic>("SELECT * FROM dealership"),
        services = await s.Many<dynamic>("SELECT * FROM service_type"),
        paymentOptions = new[] { "PayAtCheckIn" }, bookingChannel = "Web"
    });
});
app.MapPost("/appointment-quotes", async (QuoteRequest body, Actor actor, BookingService svc, CancellationToken ct) =>
    Results.Ok(await svc.Quote(body, actor, ct))).Produces<Quote>();
app.MapPost("/appointments", async (BookingRequest body, Actor actor, BookingService svc, CancellationToken ct) =>
{
    var result = await svc.Book(body, actor, ct);
    return result.Created ? Results.Created($"/appointments/{result.Booking.Appointment.Id}", result.Booking) : Results.Ok(result.Booking);
}).Produces<AppointmentView>(201).Produces<AppointmentView>();
app.MapGet("/appointments/{id:guid}", async (Guid id, Actor actor, BookingService svc, CancellationToken ct) =>
    Results.Ok(await svc.Get(id, actor, ct))).Produces<AppointmentView>();
app.MapPost("/appointments/{id:guid}/cancel", async (Guid id, ChangeRequest body, Actor actor, BookingService svc, CancellationToken ct) =>
    Results.Ok(await svc.Cancel(id, body, actor, ct))).Produces<AppointmentView>();
app.MapPost("/appointments/{id:guid}/start", async (Guid id, ChangeRequest body, Actor actor, OperationsService svc, CancellationToken ct) =>
    Results.Ok(await svc.Start(id, body, actor, ct))).Produces<AppointmentView>();
app.MapPatch("/appointments/{id:guid}/progress", async (Guid id, ProgressRequest body, Actor actor, OperationsService svc, CancellationToken ct) =>
    Results.Ok(await svc.Progress(id, body, actor, ct))).Produces<AppointmentView>();
app.MapPost("/appointments/{id:guid}/complete", async (Guid id, ChangeRequest body, Actor actor, OperationsService svc, CancellationToken ct) =>
    Results.Ok(await svc.Complete(id, body, actor, ct))).Produces<AppointmentView>();
app.MapGet("/appointments/{id:guid}/impacts", async (Guid id, Actor actor, OperationsService svc, CancellationToken ct) =>
    Results.Ok(await svc.Impacts(id, actor, ct)));
app.MapGet("/me/notifications", async (Actor actor, NotificationDispatcher svc, CancellationToken ct, int limit = 50) => Results.Ok(await svc.Inbox(actor, limit, ct)));
app.MapPost("/me/notifications/{id:guid}/read", async (Guid id, Actor actor, NotificationDispatcher svc, CancellationToken ct) => { await svc.Read(id, actor, ct); return Results.NoContent(); });
app.MapGet("/appointments/{id:guid}/notification-deliveries", async (Guid id, Actor actor, NotificationDispatcher svc, CancellationToken ct) => Results.Ok(await svc.Deliveries(id, actor, ct)));
app.MapPost("/notification-deliveries/{id:guid}/retry", async (Guid id, Actor actor, NotificationDispatcher svc, CancellationToken ct) => { await svc.Retry(id, actor, ct); return Results.Accepted(); });
await app.RunAsync();

public partial class Program;
