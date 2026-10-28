namespace Jellyfin.Api.Helpers;

/// <summary>
/// What <see cref="CspReportThrottle"/> decided about a violation report.
/// </summary>
public enum CspReportVerdict
{
    /// <summary>
    /// A new distinct violation within the window's budget: log it.
    /// </summary>
    Log,

    /// <summary>
    /// The first violation over the window's budget: log one line saying the limit was reached.
    /// </summary>
    LimitReached,

    /// <summary>
    /// A repeat of a logged violation, or one over the budget after the limit line: log nothing.
    /// </summary>
    Dropped,
}
