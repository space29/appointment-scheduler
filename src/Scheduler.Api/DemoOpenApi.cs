using Microsoft.OpenApi;

namespace Scheduler.Api;

public static class DemoOpenApi
{
    public static IServiceCollection AddSchedulerOpenApi(this IServiceCollection services) => services.AddOpenApi(options =>
        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Info.Title = "Appointment Scheduler API";
            document.Info.Description = "Local Keyloop assessment demo. Click Authorize and enter customer-a, customer-b, or staff. " +
                "Swagger UI sends that value in X-Demo-Actor. These are mock identities, not production authentication. " +
                "Start with GET /reference-data, then create a quote using a future timestamp with an explicit offset.";
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["DemoActor"] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.ApiKey,
                In = ParameterLocation.Header,
                Name = "X-Demo-Actor",
                Description = "Enter customer-a or customer-b for customer requests; staff for workshop operations."
            };

            foreach (var path in document.Paths)
            {
                if (path.Key.StartsWith("/health/", StringComparison.Ordinal) || path.Key == "/metrics") continue;
                if (path.Value.Operations == null) continue;
                foreach (var operation in path.Value.Operations.Values)
                {
                    operation.Security ??= [];
                    operation.Security.Add(new OpenApiSecurityRequirement
                    {
                        [new OpenApiSecuritySchemeReference("DemoActor", document)] = []
                    });
                }
            }
            return Task.CompletedTask;
        }));
}
