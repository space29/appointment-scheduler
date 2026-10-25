# Behavior-driven test convention

The executable specification uses xUnit plus Fluent Assertions 7.2.0. Each scenario names and separates **Given** (business context), **When** (the action), and **Then** (observable outcomes). This is a code-based BDD style; there are no Gherkin files or generated step bindings. Fluent Assertions supplies expressive assertions, while the scenario structure and business examples express behavior.

## Test organization

| File | Behavior covered | Level |
| --- | --- | --- |
| `RulesTests.cs` | Half-open reservation intervals and price rounding | Unit |
| `DomainScenarios.cs` | Aggregate lifecycle, immutable commitments, value objects, domain events, execution revisions, and recovery policy | Unit |
| `ArchitectureScenarios.cs` | Project/assembly dependency direction and aggregate mutation boundaries | Architecture |
| `PricingScenarios.cs` | Accepted offer preservation, duration fallback, local pricing bands | PostgreSQL API integration |
| `BookingScenarios.cs` | Allocation, concurrency, persistence, quote replay, rejection, transaction rollback | PostgreSQL API integration |
| `OperationsScenarios.cs` | Cancellation, actual occupancy, overrun impacts, revision checks, lifecycle races | PostgreSQL API integration |
| `NotificationScenarios.cs` | Provider outages, durable retries, expired claims, cancellation suppression, recipient access | PostgreSQL API integration |
| `ApiAccessScenarios.cs` | Identity boundaries, explicit timestamp offsets, readiness, OpenAPI | PostgreSQL API integration |
| `MigrationScenarios.cs` | Existing migration identifiers and booking data survive the project split | PostgreSQL integration |

Integration scenarios inherit `SchedulerScenario`, which resets the isolated fixture and provides common Given steps. Setup helpers verify their preconditions succeeded. The scenario's When action and Then assertions remain visible in the scenario itself. `DatabaseFixture` creates two independent API hosts sharing one temporary PostgreSQL database and supplies the controllable clock/provider. Tests are serialized within that fixture; scenarios explicitly introduce concurrency where it is the behavior under review.

## Example

```csharp
[Fact]
public async Task Given_only_unqualified_technicians_When_confirming_Then_no_booking_is_created()
{
    // Given free technicians who lack the requested service qualification.
    using var customer = Db.Client();
    await Db.Sql("DELETE FROM qualification");
    var quote = await GivenAValidQuote(customer);

    // When the customer confirms the quote.
    using var response = await customer.PostAsJsonAsync("/appointments", new BookingRequest(quote.Id));

    // Then being free is insufficient to qualify for the service.
    response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    (await Db.Scalar<int>("SELECT count(*)::int FROM appointment")).Should().Be(0);
}
```

Name scenarios `Given_<context>_When_<action>_Then_<outcome>`. Prefer customer/staff outcomes over implementation method names. Use one principal behavior per example; an explicit second When–Then can describe a meaningful replay/recovery sequence. Split unrelated outcomes into separate tests so a failure in one cannot prevent the others from running.

Use `[Theory]` examples for variations of one rule, such as pricing band boundaries or bay/technician contention. Use `.Should().Be(...)`, collection assertions, equivalence checks, and `ThrowAsync<T>()` for asynchronous failures. Group independent outcome assertions in `AssertionScope` when seeing all mismatches is useful. Keep mutation/query setup out of assertions where practical. Do not add database-dependent tests for pure rules or substitute an in-memory database for concurrency/transaction behavior.

The current suite contains 74 examples. The original BDD refactor split 31 combined cases into 51 focused examples. The Clean Architecture/DDD refactor retained those examples and added 23 domain, architecture, and migration checks. Existing pricing, payment, access, overrun, cancellation, notification, and concurrency coverage was retained. The malformed-time scenario generates a future timestamp, so rejection is attributable to its missing offset rather than an eventually stale hard-coded date.

Pure domain/rule tests and architecture checks require no database. Run the 28 examples independently with `dotnet test --filter 'FullyQualifiedName~DomainScenarios|FullyQualifiedName~RulesTests|FullyQualifiedName~ArchitectureScenarios'`. Integration tests continue to exercise real SQL constraints and transaction behavior rather than mocking those guarantees.

Run instructions and the `SCHEDULER_TEST_ADMIN` environment variable are documented in the [README](../README.md#test). To focus on one feature, use `dotnet test --filter FullyQualifiedName~BookingScenarios`. Never place real credentials in test files or configuration committed to Git.

Dependency references: [Fluent Assertions 7.2.0](https://www.nuget.org/packages/FluentAssertions/7.2.0), [assertion documentation](https://fluentassertions.com/basicassertions/), and [the pinned release's license](https://github.com/fluentassertions/fluentassertions/blob/7.2.0/LICENSE).
