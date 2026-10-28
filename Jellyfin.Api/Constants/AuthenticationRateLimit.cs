namespace Jellyfin.Api.Constants;

/// <summary>
/// The "authentication" rate limiting policy, applied per client IP to the endpoints that take a
/// password, a PIN or a quick connect secret.
/// </summary>
/// <remarks>
/// A fixed window of 60 seconds with <c>ServerConfiguration.AuthenticationRateLimitPerMinute</c> permits,
/// <see cref="DefaultPermitLimit"/> unless configured: a person who mistypes a password a few times is
/// never limited (clients retry a handful of times a minute at most), while an online guess runs at ten
/// tries a minute instead of thousands. There is no queue, so the first attempt over the limit in a window
/// is answered at once with 429 and a Retry-After header; the client only has to wait for the window to
/// roll over. A limit of zero turns the policy off.
/// </remarks>
public static class AuthenticationRateLimit
{
    /// <summary>
    /// The policy name, as used in <c>[EnableRateLimiting]</c>.
    /// </summary>
    public const string PolicyName = "authentication";

    /// <summary>
    /// Attempts allowed per client IP within one window when nothing is configured.
    /// </summary>
    public const int DefaultPermitLimit = 10;

    /// <summary>
    /// Length of the fixed window in seconds.
    /// </summary>
    public const int WindowSeconds = 60;
}
