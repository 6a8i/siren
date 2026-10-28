using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Api.Middleware;

/// <summary>
/// Refuses any request that is not GET, HEAD or OPTIONS when it authenticates with a read-only API key.
/// Runs right after authentication, so the decision does not depend on the endpoint or its policy.
/// </summary>
public class ReadOnlyApiKeyMiddleware
{
    private const string RejectionMessage = "This API key is read-only and may only make GET, HEAD and OPTIONS requests.";

    private readonly RequestDelegate _next;
    private readonly ILogger<ReadOnlyApiKeyMiddleware> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReadOnlyApiKeyMiddleware"/> class.
    /// </summary>
    /// <param name="next">The next delegate in the pipeline.</param>
    /// <param name="logger">The logger.</param>
    public ReadOnlyApiKeyMiddleware(RequestDelegate next, ILogger<ReadOnlyApiKeyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    /// <summary>
    /// Gets a value indicating whether a read-only key may make a request with the given method.
    /// </summary>
    /// <param name="method">The HTTP method.</param>
    /// <returns><c>true</c> for GET, HEAD and OPTIONS, <c>false</c> otherwise.</returns>
    public static bool IsReadMethod(string method)
        => HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

    /// <summary>
    /// Executes the middleware action.
    /// </summary>
    /// <param name="httpContext">The current HTTP context.</param>
    /// <returns>The async task.</returns>
    public async Task Invoke(HttpContext httpContext)
    {
        if (httpContext.User.GetIsReadOnlyApiKey() && !IsReadMethod(httpContext.Request.Method))
        {
            _logger.LogWarning(
                "Refusing {Method} {Path} made with the read-only API key of {Client}",
                httpContext.Request.Method,
                httpContext.Request.Path,
                httpContext.User.GetClient());

            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsync(RejectionMessage, httpContext.RequestAborted).ConfigureAwait(false);
            return;
        }

        await _next(httpContext).ConfigureAwait(false);
    }
}
