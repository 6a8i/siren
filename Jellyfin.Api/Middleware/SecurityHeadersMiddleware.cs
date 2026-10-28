using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Adds the browser security headers to every response: static files, the API and error pages alike.
/// The headers only say what the server is not (not a frame target for other sites, not a guessable
/// content type, not a source of full referrers) and need nothing from the web client; a full
/// Content-Security-Policy is deliberately not set, so clients and plugins keep working unchanged.
/// </summary>
public class SecurityHeadersMiddleware
{
    private const string ReferrerPolicyHeader = "Referrer-Policy";
    private const string PermissionsPolicyHeader = "Permissions-Policy";

    private static readonly StringValues _noSniff = new("nosniff");
    private static readonly StringValues _referrerPolicy = new("strict-origin-when-cross-origin");
    private static readonly StringValues _frameOptions = new("SAMEORIGIN");
    private static readonly StringValues _frameAncestors = new("frame-ancestors 'self'");
    private static readonly StringValues _permissionsPolicy = new("camera=(), microphone=(), geolocation=()");

    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityHeadersMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Executes the middleware action.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <param name="serverConfigurationManager">The server configuration manager.</param>
    /// <returns>The async task.</returns>
    public Task Invoke(HttpContext httpContext, IServerConfigurationManager serverConfigurationManager)
    {
        // Set up front rather than in OnStarting: nothing downstream clears headers (the exception
        // middleware only writes when the response has not started), so the headers reach streamed
        // responses, HEAD and OPTIONS answers and static files the same way, with no callback per request.
        if (serverConfigurationManager.Configuration.EnableSecurityHeaders)
        {
            var headers = httpContext.Response.Headers;
            headers.XContentTypeOptions = _noSniff;
            headers[ReferrerPolicyHeader] = _referrerPolicy;

            // X-Frame-Options for older browsers, frame-ancestors for the ones that honor CSP; both say the
            // same thing. EnableSecurityHeaders=false is the switch for embedding the web client elsewhere.
            headers.XFrameOptions = _frameOptions;
            headers.ContentSecurityPolicy = _frameAncestors;
            headers[PermissionsPolicyHeader] = _permissionsPolicy;
        }

        return _next(httpContext);
    }
}
