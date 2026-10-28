using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Api.Helpers;

/// <summary>
/// Decides which Content-Security-Policy violation reports get a log line. Browsers send one report per
/// blocked resource per page load, so a single tab with a broken plugin page could write thousands of
/// identical warnings; instead, each distinct violation is logged once per window, at most
/// <see cref="Limit"/> of them, and the rest are counted and summarised in one line.
/// </summary>
public sealed class CspReportThrottle
{
    /// <summary>
    /// The default number of distinct violations logged per window.
    /// </summary>
    public const int DefaultLimit = 20;

    /// <summary>
    /// The default window length.
    /// </summary>
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal);

    private DateTimeOffset _windowStart;
    private bool _windowOpen;
    private bool _limitReached;
    private int _dropped;

    /// <summary>
    /// Initializes a new instance of the <see cref="CspReportThrottle"/> class with the defaults.
    /// </summary>
    public CspReportThrottle()
        : this(DefaultLimit, DefaultWindow, TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="CspReportThrottle"/> class.
    /// </summary>
    /// <param name="limit">Distinct violations logged per window.</param>
    /// <param name="window">Length of the window.</param>
    /// <param name="timeProvider">The clock.</param>
    public CspReportThrottle(int limit, TimeSpan window, TimeProvider timeProvider)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Limit = limit;
        Window = window;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Gets the number of distinct violations logged per window.
    /// </summary>
    public int Limit { get; }

    /// <summary>
    /// Gets the window length.
    /// </summary>
    public TimeSpan Window { get; }

    /// <summary>
    /// Registers a report and says what to do with it.
    /// </summary>
    /// <param name="key">What makes the violation distinct (document, directive, blocked and source URIs).</param>
    /// <param name="droppedInPreviousWindow">
    /// When this call opened a new window, how many reports of the previous one were not logged; the
    /// caller writes the summary line for them. Zero otherwise.
    /// </param>
    /// <returns>Whether to log the report, log that the limit was reached, or stay quiet.</returns>
    public CspReportVerdict Register(string key, out int droppedInPreviousWindow)
    {
        ArgumentNullException.ThrowIfNull(key);

        var now = _timeProvider.GetUtcNow();
        lock (_lock)
        {
            droppedInPreviousWindow = 0;
            if (!_windowOpen || now - _windowStart >= Window)
            {
                droppedInPreviousWindow = _dropped;
                _windowStart = now;
                _windowOpen = true;
                _limitReached = false;
                _dropped = 0;
                _logged.Clear();
            }

            if (_logged.Contains(key))
            {
                _dropped++;
                return CspReportVerdict.Dropped;
            }

            if (_logged.Count < Limit)
            {
                _logged.Add(key);
                return CspReportVerdict.Log;
            }

            _dropped++;
            if (_limitReached)
            {
                return CspReportVerdict.Dropped;
            }

            _limitReached = true;
            return CspReportVerdict.LimitReached;
        }
    }
}
