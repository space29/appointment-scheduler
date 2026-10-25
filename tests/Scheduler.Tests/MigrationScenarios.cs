using FluentAssertions;
using Npgsql;
using Scheduler.Infrastructure;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class MigrationScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_legacy_migration_identifiers_When_the_new_infrastructure_runner_runs_Then_existing_data_is_preserved()
    {
        // Given a populated database with the pre-refactor migration names.
        using var customer = Db.Client();
        var booking = await GivenAConfirmedBooking(customer);
        var names = await Db.Scalar<string[]>("SELECT array_agg(name ORDER BY name) FROM schema_migration");
        names.Should().Equal("Scheduler.Api.Database.001_initial.sql", "Scheduler.Api.Database.002_demo_seed.sql", "Scheduler.Api.Database.003_outbox_trace_context.sql");
        // Recreate the pre-observability schema with real, already committed business data.
        await Db.Sql("""
            ALTER TABLE outbox_message DROP COLUMN trace_parent, DROP COLUMN trace_state;
            DELETE FROM schema_migration WHERE name='Scheduler.Api.Database.003_outbox_trace_context.sql';
            """);
        await using var source = NpgsqlDataSource.Create(Db.Connection);

        // When the infrastructure assembly applies pending migrations twice.
        await new Database(source).Migrate(true);
        await new Database(source).Migrate(true);

        // Then the initial schema is not replayed and the existing booking remains intact.
        (await Db.Scalar<int>("SELECT count(*)::int FROM schema_migration")).Should().Be(3);
        (await GetBooking(customer, booking.Appointment.Id)).Should().BeEquivalentTo(booking);
        (await Db.Scalar<int>("SELECT count(*)::int FROM outbox_message WHERE trace_parent IS NULL")).Should().Be(1);
    }
}
