using System;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Adds the browser security headers to every response: static files, the API and error pages alike.
/// The fixed headers only say what the server is not (not a frame target for other sites, not a guessable
/// content type, not a source of full referrers) and need nothing from the web client. The full
/// Content-Security-Policy is sent according to <see cref="ServerConfiguration.ContentSecurityPolicyMode"/>:
/// report-only by default, so a client or plugin it would break shows up in the log before it is enforced.
/// </summary>
public class SecurityHeadersMiddleware
{
    /// <summary>
    /// The path, below the base URL, that browsers send violation reports to.
    /// </summary>
    public const string ReportPath = "/System/CspReport";

    /// <summary>
    /// The full policy without its <c>report-uri</c>, which depends on the base URL. Every allowance is
    /// there for something the Jellyfin web client or its plugins do today:
    /// <list type="bullet">
    /// <item><c>script-src 'unsafe-inline'</c>: index.html carries inline bootstrap scripts, and plugin
    /// configuration pages are HTML fragments with their own inline scripts.</item>
    /// <item><c>script-src 'unsafe-eval'</c>: some bundled libraries (the ES5 shims, template helpers)
    /// still build functions from strings; a page using them breaks without it.</item>
    /// <item><c>script-src blob:</c>, <c>worker-src blob:</c>, <c>child-src blob:</c>: the subtitle
    /// renderer (libass), the PDF reader and the audio analysers run as workers created from blob URLs.</item>
    /// <item><c>style-src 'unsafe-inline'</c>: layout and theme switching set inline styles, and skins
    /// inject style elements at runtime.</item>
    /// <item><c>img-src data: blob: https:</c>: blurhash placeholders and generated thumbnails are data or
    /// blob URLs; metadata provider images are loaded straight from the provider when they are not
    /// proxied through the server.</item>
    /// <item><c>media-src blob: data:</c>: HLS and media-source playback feed the video element from
    /// blob URLs; small notification sounds are data URLs.</item>
    /// <item><c>font-src data:</c>: icon fonts are inlined by the bundler.</item>
    /// <item><c>connect-src ws: wss: https:</c>: the session websocket on both schemes, and clients that
    /// talk to a metadata or plugin service directly.</item>
    /// <item><c>frame-src https:</c>: plugin pages and trailers may embed remote content; same-origin
    /// frames are covered by <c>'self'</c>.</item>
    /// <item><c>frame-ancestors 'self'</c>, <c>base-uri 'self'</c>, <c>form-action 'self'</c>: the
    /// restrictions that make the policy worth sending; none of them is needed by the client.</item>
    /// </list>
    /// </summary>
    private const string PolicyWithoutReportUri =
        "default-src 'self'; "
        + "script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: blob: https:; "
        + "media-src 'self' blob: data:; "
        + "font-src 'self' data:; "
        + "connect-src 'self' ws: wss: https:; "
        + "worker-src 'self' blob:; "
        + "child-src 'self' blob:; "
        + "frame-src 'self' https:; "
        + "frame-ancestors 'self'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "report-uri ";

    private const string ReferrerPolicyHeader = "Referrer-Policy";
    private const string PermissionsPolicyHeader = "Permissions-Policy";

    private static readonly StringValues _noSniff = new("nosniff");
    private static readonly StringValues _referrerPolicy = new("strict-origin-when-cross-origin");
    private static readonly StringValues _frameOptions = new("SAMEORIGIN");
    private static readonly StringValues _frameAncestors = new("frame-ancestors 'self'");
    private static readonly StringValues _permissionsPolicy = new("camera=(), microphone=(), geolocation=()");

    private readonly RequestDelegate _next;

    // The base URL is fixed for the life of the pipeline (changing it restarts the server), so the policy
    // string is built once and reused; the check is only there for the first request.
    private CachedPolicy? _policy;

    /// <summary>
    /// Initializes a new instance of the <see cref="SecurityHeadersMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    public SecurityHeadersMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>
    /// Builds the full policy for a base URL.
    /// </summary>
    /// <param name="pathBase">The base URL the server is mapped under, empty when there is none.</param>
    /// <returns>The policy, ending with the report-uri below that base URL.</returns>
    public static string BuildPolicy(PathString pathBase)
        => string.Concat(PolicyWithoutReportUri, pathBase.Value, ReportPath);

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
        var configuration = serverConfigurationManager.Configuration;
        if (configuration.EnableSecurityHeaders)
        {
            var headers = httpContext.Response.Headers;
            headers.XContentTypeOptions = _noSniff;
            headers[ReferrerPolicyHeader] = _referrerPolicy;

            // X-Frame-Options for older browsers, frame-ancestors for the ones that honor CSP; both say the
            // same thing. EnableSecurityHeaders=false is the switch for embedding the web client elsewhere.
            headers.XFrameOptions = _frameOptions;
            headers[PermissionsPolicyHeader] = _permissionsPolicy;

            switch (configuration.ContentSecurityPolicyMode)
            {
                case ContentSecurityPolicyMode.Enforce:
                    headers.ContentSecurityPolicy = GetPolicy(httpContext.Request.PathBase);
                    break;
                case ContentSecurityPolicyMode.ReportOnly:
                    // The report-only header never blocks, so the frame restriction keeps its own enforcing
                    // header; the full policy repeats it so a report for it would show up too.
                    headers.ContentSecurityPolicy = _frameAncestors;
                    headers.ContentSecurityPolicyReportOnly = GetPolicy(httpContext.Request.PathBase);
                    break;
                default:
                    headers.ContentSecurityPolicy = _frameAncestors;
                    break;
            }
        }

        return _next(httpContext);
    }

    private StringValues GetPolicy(PathString pathBase)
    {
        var basePath = pathBase.Value ?? string.Empty;
        var cached = _policy;
        if (cached is null || !string.Equals(cached.PathBase, basePath, StringComparison.Ordinal))
        {
            cached = new CachedPolicy(basePath, new StringValues(BuildPolicy(pathBase)));
            _policy = cached;
        }

        return cached.Header;
    }

    private sealed record CachedPolicy(string PathBase, StringValues Header);
}
