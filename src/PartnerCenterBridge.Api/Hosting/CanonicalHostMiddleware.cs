namespace PartnerCenterBridge.Api.Hosting;

/// <summary>
/// Local profile: WebAuthn binds passkeys to one origin (<c>http://localhost:port</c>), so requests
/// addressed to a loopback IP literal (127.0.0.1 or [::1]) are sent to the canonical origin. GET
/// and HEAD get a redirect with the same path and query; anything else gets 421 because silently
/// replaying a body at another origin is not something a client should rely on. This only
/// normalizes the origin -- it grants nothing, and no request is trusted for being local.
/// </summary>
public sealed class CanonicalHostMiddleware
{
    private readonly RequestDelegate _next;
    private readonly LocalWorkbenchOptions _options;

    public CanonicalHostMiddleware(RequestDelegate next, LocalWorkbenchOptions options)
    {
        _next = next;
        _options = options;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!IsLoopbackLiteral(context.Request.Host.Host)) return _next(context);

        var request = context.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
        {
            var target = _options.CanonicalUrl + request.PathBase + request.Path + request.QueryString;
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = target;
            return Task.CompletedTask;
        }

        context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync($"Use {_options.CanonicalUrl} for this request.");
    }

    internal static bool IsLoopbackLiteral(string host) =>
        host is "127.0.0.1" or "[::1]" or "::1";
}
