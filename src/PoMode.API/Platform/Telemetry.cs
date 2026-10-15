using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;

namespace PoMode.API.Platform;

/// <summary>
/// Traces, metrics and logs to the shared Application Insights component, through the Azure Monitor
/// OpenTelemetry distro. On only where <c>APPLICATIONINSIGHTS_CONNECTION_STRING</c> is set, which the
/// Bicep does for the deployed app and nothing does locally, so a developer machine exports nothing.
///
/// <para>The OpenTelemetry export was removed once because no collector was configured anywhere. The
/// collector existed all along (the App Service already carried the connection string), so this is
/// that export, pointed at it. It signs in with the managed identity (<see cref="DefaultAzureCredential"/>
/// and the Monitoring Metrics Publisher role in monitoring-role.bicep) rather than relying on the
/// connection string's instrumentation key.</para>
/// </summary>
public static class Telemetry
{
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    public static bool IsEnabled(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[ConnectionStringKey]);

    public static WebApplicationBuilder AddPoTelemetry(this WebApplicationBuilder builder)
    {
        if (IsEnabled(builder.Configuration))
        {
            builder.Services.AddOpenTelemetry().UseAzureMonitor(options =>
            {
                options.ConnectionString = builder.Configuration[ConnectionStringKey];
                options.Credential = new DefaultAzureCredential();
            });
        }
        return builder;
    }
}
