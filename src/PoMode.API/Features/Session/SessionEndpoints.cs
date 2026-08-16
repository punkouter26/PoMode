using System.Security.Claims;
using PoMode.Shared.Session;

namespace PoMode.API.Features.Session;

public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSession(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/session").RequireAuthorization();

        group.MapGet("", (ClaimsPrincipal user) => TypedResults.Ok(new SessionInfo(
            user.Identity?.Name ?? "unknown",
            user.FindAll(ClaimTypes.Role).Select(claim => claim.Value).ToArray())));

        return app;
    }
}
