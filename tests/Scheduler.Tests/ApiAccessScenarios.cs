using System.Net;
using System.Net.Http.Json;
using System.Text;
using FluentAssertions;
using Scheduler.Api;
using Xunit;

namespace Scheduler.Tests;

[Collection("PostgreSQL")]
public sealed class ApiAccessScenarios(DatabaseFixture database) : SchedulerScenario(database)
{
    [Fact]
    public async Task Given_no_demo_identity_When_reference_data_is_requested_Then_authentication_is_required()
    {
        // Given a request with no configured demonstration identity.
        using var anonymous = Db.First.CreateClient();

        // When it accesses a business endpoint.
        using var response = await anonymous.GetAsync("/reference-data");

        // Then anonymous access is rejected.
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Given_a_timestamp_without_an_offset_When_a_quote_is_requested_Then_the_ambiguous_time_is_rejected()
    {
        // Given an otherwise valid request with a future timestamp missing its offset.
        using var customer = Db.Client();
        var localTimestamp = Db.Slot.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var body = $$"""{"customerId":"{{DemoIds.CustomerA}}","vehicleId":"{{VehicleA}}","dealershipId":"{{DemoIds.Dealership}}","serviceTypeId":"{{DemoIds.Service}}","startsAt":"{{localTimestamp}}"}""";
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        // When the client requests a quote using that ambiguous instant.
        using var response = await customer.PostAsync("/appointment-quotes", content);

        // Then the timestamp is rejected before any quote is persisted.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Db.Scalar<int>("SELECT count(*)::int FROM appointment_quote")).Should().Be(0);
    }

    [Fact]
    public async Task Given_a_customer_identity_When_it_starts_work_Then_staff_permission_is_required()
    {
        // Given a customer who owns an appointment but is not workshop staff.
        using var customer = Db.Client();
        var booking = await GivenAConfirmedBooking(customer);

        // When that customer invokes a staff-only lifecycle operation.
        using var response = await customer.PostAsJsonAsync($"/appointments/{booking.Appointment.Id}/start", new ChangeRequest(1, "Attempt start"));

        // Then ownership does not grant permission to start workshop work.
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Db.Scalar<int>("SELECT count(*)::int FROM job_execution")).Should().Be(0);
    }

    [Fact]
    public async Task Given_a_ready_service_When_readiness_is_requested_Then_it_reports_success_without_demo_identity()
    {
        // Given a running application connected to its migrated test database.
        using var probe = Db.First.CreateClient();

        // When infrastructure checks readiness.
        using var response = await probe.GetAsync("/health/ready");

        // Then the unauthenticated operational probe succeeds.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Given_the_mock_client_needs_a_contract_When_OpenAPI_is_requested_Then_the_booking_contract_is_available()
    {
        // Given a running local assessment API.
        using var client = Db.First.CreateClient();

        // When its client contract is retrieved.
        var contract = await client.GetStringAsync("/openapi/v1.json");

        // Then the quote operation is included in that contract.
        contract.Should().Contain("/appointment-quotes");
    }
}
