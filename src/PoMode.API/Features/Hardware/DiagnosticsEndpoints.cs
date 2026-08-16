namespace PoMode.API.Features.Hardware;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnostics(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health", () => "Healthy");
        var group = app.MapGroup("/diag");
        group.MapGet("", (DiagnosticsService diagnostics) => TypedResults.Ok(diagnostics.BuildReport()));
        return app;
    }
}
