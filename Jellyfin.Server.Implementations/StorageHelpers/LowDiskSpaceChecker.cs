using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.System;

namespace Jellyfin.Server.Implementations.StorageHelpers;

/// <summary>
/// Decides which drives deserve a low disk space warning. Pure logic over the storage info the
/// caller measured, so it can be tested without a filesystem; the caller is the scheduled task.
/// </summary>
public class LowDiskSpaceChecker
{
    /// <summary>
    /// Bytes in a gigabyte, as a drive vendor counts them (and as the dashboard shows them).
    /// </summary>
    public const long BytesPerGb = 1_000_000_000;

    /// <summary>
    /// How long a drive stays quiet after a warning, so a drive that is full all week yields one
    /// activity log entry a day and not one per run.
    /// </summary>
    public static readonly TimeSpan WarningInterval = TimeSpan.FromDays(1);

    private readonly Dictionary<string, DateTime> _lastWarned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Evaluates the measured folders against the threshold.
    /// </summary>
    /// <param name="folders">The storage info of each folder to watch, as <c>StorageHelper.GetFreeSpaceOf</c> reports it.</param>
    /// <param name="thresholdGb">The free space, in GB, below which a drive is warned about; zero or less disables the check.</param>
    /// <param name="now">The current time, used for the once-a-day rule.</param>
    /// <returns>One warning per drive that is below the threshold and was not warned about in the last day.</returns>
    public IReadOnlyList<LowDiskSpaceWarning> Evaluate(IEnumerable<FolderStorageInfo> folders, int thresholdGb, DateTime now)
    {
        if (thresholdGb <= 0)
        {
            return [];
        }

        var thresholdBytes = thresholdGb * BytesPerGb;
        var warnings = new List<LowDiskSpaceWarning>();

        // Several folders usually share one drive; the warning names all of them once.
        foreach (var drive in folders
            .Where(f => f.FreeSpace >= 0 && !string.IsNullOrEmpty(f.DeviceId))
            .GroupBy(f => f.DeviceId!, StringComparer.OrdinalIgnoreCase))
        {
            var freeBytes = drive.Min(f => f.FreeSpace);
            if (freeBytes >= thresholdBytes)
            {
                // Back above the line: the next time it drops, warn right away.
                _lastWarned.Remove(drive.Key);
                continue;
            }

            if (_lastWarned.TryGetValue(drive.Key, out var last) && now - last < WarningInterval)
            {
                continue;
            }

            _lastWarned[drive.Key] = now;
            warnings.Add(new LowDiskSpaceWarning(drive.Key, drive.Select(f => f.Path).Distinct(StringComparer.Ordinal).ToArray(), freeBytes, thresholdGb));
        }

        return warnings;
    }
}
