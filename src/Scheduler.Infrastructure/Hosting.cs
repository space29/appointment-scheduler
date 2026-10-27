using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Scheduler.Application;

namespace Scheduler.Infrastructure;

public static class Hosting
{
    public static IServiceCollection AddSchedulerInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
        services.AddSingleton(_ => NpgsqlDataSource.Create(configuration.GetConnectionString("Scheduler")
            ?? throw new InvalidOperationException("Set ConnectionStrings__Scheduler or a .NET user secret. Never commit credentials.")));
        services.AddSingleton<Database>();
        services.AddSingleton<ReferenceDataCache>();
        services.AddSingleton<ISchedulingStore, PostgresSchedulingStore>();
        services.AddSingleton<INotificationStore, PostgresNotificationStore>();
        services.AddSingleton<IReferenceDataReader, PostgresReferenceDataReader>();
        services.AddSingleton<IStaffDirectory, DemoStaffDirectory>();
        services.AddSingleton<IDeliveryProvider, MockDeliveryProvider>();
        services.AddHostedService<NotificationWorker>();
        services.AddSingleton<OperationalMetrics>();
        services.AddHostedService(sp => sp.GetRequiredService<OperationalMetrics>());
        return services;
    }
}
internal sealed class DemoStaffDirectory : IStaffDirectory
{
    public Guid[] Recipients(Guid dealershipId) => dealershipId == DemoIds.Dealership ? [DemoIds.Staff] : [];
}
public sealed class MockDeliveryProvider(IConfiguration config, ILogger<MockDeliveryProvider> logger) : IDeliveryProvider
{
    public Task Send(Guid deliveryId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (config.GetValue<bool>("Notifications:SimulateFailure")) throw new DeliveryUnavailableException();
        logger.LogInformation("Mock provider accepted delivery {DeliveryId}", deliveryId);
        return Task.CompletedTask;
    }
}
internal sealed class NotificationWorker(NotificationDispatcher dispatcher, IConfiguration config, ILogger<NotificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Notifications:Enabled", true)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await dispatcher.DispatchBatch(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError("Notification worker failed with {ErrorType}; durable work will be retried", ex.GetType().Name); }
            try { await Task.Delay(Math.Clamp(config.GetValue("Notifications:PollMilliseconds", 500), 50, 10000), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
