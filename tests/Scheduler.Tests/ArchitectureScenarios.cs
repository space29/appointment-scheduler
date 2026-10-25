using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Scheduler.Application;
using Scheduler.Domain;
using Scheduler.Infrastructure;
using Xunit;

namespace Scheduler.Tests;

public sealed class ArchitectureScenarios
{
    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "AppointmentScheduler.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private static XDocument Project(string name) => XDocument.Load(Path.Combine(Root(), "src", name, name + ".csproj"));

    [Fact]
    public void Given_the_domain_project_When_dependencies_are_inspected_Then_it_is_independent_of_frameworks_and_other_layers()
    {
        // Given the compiled domain and its declared project dependencies.
        var assembly = typeof(ServiceAppointment).Assembly;
        var project = Project("Scheduler.Domain");

        // When its dependency boundary is inspected.
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name!);

        // Then business rules require only the .NET base libraries.
        references.Should().OnlyContain(n => n.StartsWith("System.") || n == "netstandard");
        project.Descendants("ProjectReference").Should().BeEmpty();
        project.Descendants("PackageReference").Should().BeEmpty();
        project.Descendants("FrameworkReference").Should().BeEmpty();
    }

    [Fact]
    public void Given_the_application_project_When_dependencies_are_inspected_Then_it_depends_only_inward()
    {
        // Given the application use cases and repository interfaces.
        var project = Project("Scheduler.Application");

        // When their declared and compiled dependencies are inspected.
        var projects = project.Descendants("ProjectReference").Select(x => (string)x.Attribute("Include")!);
        var assemblies = typeof(BookingService).Assembly.GetReferencedAssemblies().Select(a => a.Name!);

        // Then SQL, hosting, transport, and concrete adapters cannot leak into use cases.
        projects.Should().Equal("../Scheduler.Domain/Scheduler.Domain.csproj");
        assemblies.Should().NotContain(n => n == "Scheduler.Api" || n == "Scheduler.Infrastructure" || n == "Npgsql" || n == "Dapper" || n.StartsWith("Microsoft.AspNetCore"));
        project.Descendants("FrameworkReference").Should().BeEmpty();
    }

    [Fact]
    public void Given_infrastructure_and_API_When_dependencies_are_inspected_Then_adapters_point_inward_and_endpoints_do_not_use_SQL_drivers()
    {
        // Given the adapter project and HTTP entry point.
        var infrastructure = Project("Scheduler.Infrastructure");
        var api = typeof(Program).Assembly;

        // When inspecting adapter direction and the API's compiled references.
        var references = infrastructure.Descendants("ProjectReference").Select(x => (string)x.Attribute("Include")!);
        var apiAssemblies = api.GetReferencedAssemblies().Select(a => a.Name!);

        // Then Infrastructure implements Application ports, while SQL drivers stay out of endpoint code.
        references.Should().Equal("../Scheduler.Application/Scheduler.Application.csproj");
        apiAssemblies.Should().NotContain("Npgsql").And.NotContain("Dapper");
        typeof(ISchedulingStore).IsAssignableFrom(typeof(PostgresSchedulingStore)).Should().BeTrue();
    }

    [Fact]
    public void Given_domain_entities_When_public_mutators_are_inspected_Then_state_cannot_be_changed_through_property_setters()
    {
        // Given the aggregate and its owned execution entity.
        var types = new[] { typeof(ServiceAppointment), typeof(WorkExecution) };

        // When inspecting publicly writable properties.
        var publicSetters = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.SetMethod?.IsPublic == true);

        // Then clients must invoke domain behavior rather than assign lifecycle state directly.
        publicSetters.Should().BeEmpty();
    }
}
