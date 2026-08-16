namespace PoMode.API.Infrastructure;

public static class EnvironmentDetector
{
    public static bool IsAzureHosted() =>
        Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID") is not null
        || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
}
