using System;
using System.Collections.Generic;
using System.Net;

namespace Jellyfin.Server.RateLimiting;

/// <summary>
/// Counts the rejections of the authentication rate limiter per client address over a sliding hour,
/// so that an address that keeps hitting the limit (a password guess that waits out each window) is
/// reported once an hour instead of drowning the log in one line per 429.
/// </summary>
public sealed class AuthenticationRateLimitMonitor
{
    /// <summary>
    /// Rejections within <see cref="Window"/> after which an address is reported.
    /// </summary>
    public const int Threshold = 50;

    /// <summary>
    /// Addresses tracked at most; the quiet ones are dropped first when the table is full.
    /// </summary>
    public const int MaxAddresses = 1024;

    /// <summary>
    /// The sliding window the rejections are counted in, and the least time between two reports of
    /// the same address.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = new();

    /// <summary>
    /// Records one rejection of an address.
    /// </summary>
    /// <param name="address">The client address.</param>
    /// <param name="now">The current time.</param>
    /// <param name="count">The address's rejections within the window, this one included.</param>
    /// <returns><c>true</c> when the address passed <see cref="Threshold"/> and was not reported within the window: the caller reports it.</returns>
    public bool Record(IPAddress address, DateTime now, out int count)
    {
        ArgumentNullException.ThrowIfNull(address);

        lock (_lock)
        {
            if (!_entries.TryGetValue(address, out var entry))
            {
                if (_entries.Count >= MaxAddresses)
                {
                    Prune(now);
                }

                if (_entries.Count >= MaxAddresses)
                {
                    // Still full of busy addresses: this one is not tracked rather than growing without bound.
                    count = 1;
                    return false;
                }

                entry = new Entry();
                _entries[address] = entry;
            }

            entry.Rejections.Enqueue(now);
            var windowStart = now - Window;
            while (entry.Rejections.Count > 0 && entry.Rejections.Peek() < windowStart)
            {
                entry.Rejections.Dequeue();
            }

            count = entry.Rejections.Count;
            if (count < Threshold || (entry.LastReported.HasValue && now - entry.LastReported.Value < Window))
            {
                return false;
            }

            entry.LastReported = now;
            return true;
        }
    }

    private void Prune(DateTime now)
    {
        var windowStart = now - Window;
        var stale = new List<IPAddress>();
        foreach (var (address, entry) in _entries)
        {
            while (entry.Rejections.Count > 0 && entry.Rejections.Peek() < windowStart)
            {
                entry.Rejections.Dequeue();
            }

            if (entry.Rejections.Count == 0)
            {
                stale.Add(address);
            }
        }

        foreach (var address in stale)
        {
            _entries.Remove(address);
        }
    }

    private sealed class Entry
    {
        public Queue<DateTime> Rejections { get; } = new();

        public DateTime? LastReported { get; set; }
    }
}
