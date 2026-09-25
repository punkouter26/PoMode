using Microsoft.AspNetCore.Http.HttpResults;
using PoMode.API.Infrastructure;

namespace PoMode.API.Features.PitchTracking;

/// <summary>
/// Serves the browser inference runtime (onnxruntime-web + the Basic Pitch model) from our own
/// origin for the ClientDelegated pitch tier. The assets are pinned and SHA-256-verified by
/// <see cref="ModelRegistry"/> exactly like the local ONNX models, downloaded on first use, and
/// never committed — so the app keeps working offline after that first use and no CDN is in the
/// serving path (Phase 8 plan ruling).
/// </summary>
public static class WebRuntimeEndpoints
{
    public static IEndpointRouteBuilder MapWebRuntime(this IEndpointRouteBuilder app)
    {
        // The route value selects from this fixed allow-list and never becomes part of a path —
        // the same no-traversal-surface rule the stem endpoint follows (spec §13.7).
        var assets = ModelCatalog.WebRuntime.ToDictionary(d => d.FileName);

        var group = app.MapGroup("/web-runtime");

        group.MapGet("/{asset}", async Task<Results<FileContentHttpResult, NotFound>> (
            string asset, ModelRegistry registry, CancellationToken ct) =>
        {
            if (!assets.TryGetValue(asset, out var descriptor))
            {
                return TypedResults.NotFound();
            }

            // Downloaded on first request and SHA-256-verified, Azure included: these files run in
            // the browser, which is Tier 2's whole point on a host that cannot run models itself.
            var path = await registry.EnsureAsync(descriptor, ct, servedToBrowser: true);

            var contentType = Path.GetExtension(descriptor.FileName) switch
            {
                ".mjs" => "text/javascript",
                ".wasm" => "application/wasm",
                _ => "application/octet-stream",
            };

            // Byte copy rather than PhysicalFile: streaming a file in place while the registry
            // could be writing it is the same handle race §13.4 documents for job artifacts.
            var bytes = await File.ReadAllBytesAsync(path, ct);
            return TypedResults.File(bytes, contentType);
        });

        return app;
    }
}
