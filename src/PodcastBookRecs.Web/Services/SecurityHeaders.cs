namespace PodcastBookRecs.Web.Services;

/// <summary>
/// Response headers that every page and asset gets. Framing is handled separately, by the
/// <c>frame-ancestors</c> policy on the Razor components endpoint.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>
    /// <c>nosniff</c> stops a browser running a response as a script or stylesheet when its content type says otherwise.
    /// <c>no-referrer</c> keeps page addresses out of requests to other sites; the app never needs a Referer.
    /// </summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            await next(context);
        });
}
