namespace PoMode.API.Features.Hardware;

public static class DiagnosticsEndpoints
{
    public static IEndpointRouteBuilder MapDiagnostics(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/diag");
        group.MapGet("", async (DiagnosticsService diagnostics, CancellationToken ct)
            => TypedResults.Ok(await diagnostics.BuildReportAsync(ct)));
        return app;
    }
}
