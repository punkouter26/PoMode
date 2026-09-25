using tusdotnet;

namespace PoMode.API.Features.Uploads;

public static class ResumableUploadEndpoints
{
    /// <summary>
    /// The tus endpoint. Deliberately outside <c>/api/analysis</c>: creating an upload is a POST, and
    /// the analysis group's POSTs each start a job — keeping the transport apart keeps that true.
    /// Not rate-limited: every chunk is a request, and a 100 MB memo in 8 MB chunks would spend a
    /// client's whole minute on one file. The finalize call that queues the job carries the limit.
    /// </summary>
    public static IEndpointRouteBuilder MapResumableUploads(this IEndpointRouteBuilder app)
    {
        app.MapTus(ResumableUploads.Route,
                context => context.RequestServices.GetRequiredService<ResumableUploads>().ConfigureAsync(context))
            .RequireAuthorization()
            .WithTags("Uploads")
            .ExcludeFromDescription();
        return app;
    }
}
