using System;
using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.StorageHelpers;

/// <summary>
/// A drive below the free space threshold, as <see cref="LowDiskSpaceChecker"/> reports it.
/// </summary>
/// <param name="DeviceId">The drive, as <see cref="MediaBrowser.Model.System.FolderStorageInfo.DeviceId"/> names it.</param>
/// <param name="Paths">The watched folders that live on it.</param>
/// <param name="FreeBytes">The free space on it.</param>
/// <param name="ThresholdGb">The threshold it is below.</param>
public record LowDiskSpaceWarning(string DeviceId, IReadOnlyList<string> Paths, long FreeBytes, int ThresholdGb)
{
    /// <summary>
    /// Gets the free space in GB with one decimal, for log lines and the activity entry.
    /// </summary>
    public double FreeGb => Math.Round(FreeBytes / (double)LowDiskSpaceChecker.BytesPerGb, 1);
}
