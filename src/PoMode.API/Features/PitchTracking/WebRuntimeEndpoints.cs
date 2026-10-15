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

        group.MapGet("/{asset}", async Task<Results<FileContentHttpResult, StatusCodeHttpResult, NotFound>> (
            string asset, ModelRegistry registry, HttpContext http, CancellationToken ct) =>
        {
            if (!assets.TryGetValue(asset, out var descriptor))
            {
                return TypedResults.NotFound();
            }

            // The pinned SHA-256 is the version, so it is the ETag. no-cache makes the browser ask every
            // time, but a browser that already has the file gets a 304 before the server reads up to
            // 67 MB off disk to send it again.
            var etag = $"\"{descriptor.Sha256}\"";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.ETag = etag;
            if (http.Request.Headers.IfNoneMatch.ToString().Contains(etag, StringComparison.Ordinal))
            {
                return TypedResults.StatusCode(StatusCodes.Status304NotModified);
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
