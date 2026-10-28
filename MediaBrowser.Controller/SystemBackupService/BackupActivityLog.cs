using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.Controller.SystemBackupService;

/// <summary>
/// Writes the outcome of a backup to the activity log. Shared by the scheduled task and the API so both
/// paths leave the same trace for an administrator who only looks at the dashboard.
/// </summary>
public static class BackupActivityLog
{
    /// <summary>
    /// Activity log type of a backup that was written.
    /// </summary>
    public const string CreatedType = "BackupCreated";

    /// <summary>
    /// Activity log type of a backup that failed.
    /// </summary>
    public const string FailedType = "BackupFailed";

    private static readonly string[] _sizeSuffixes = ["B", "KiB", "MiB", "GiB", "TiB"];

    /// <summary>
    /// Records that a backup archive was written.
    /// </summary>
    /// <param name="activityManager">The activity manager.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="archivePath">The path of the archive.</param>
    /// <returns>A task representing the write.</returns>
    public static Task LogCreatedAsync(IActivityManager activityManager, ILocalizationManager localization, string archivePath)
    {
        long size = 0;
        try
        {
            size = new FileInfo(archivePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The entry is about the backup having happened; the size is a courtesy.
        }

        return activityManager.CreateAsync(new ActivityLog(
            string.Format(
                CultureInfo.InvariantCulture,
                localization.GetServerLocalizedString("BackupCreatedWithNameAndSize"),
                Path.GetFileName(archivePath),
                HumanizeSize(size)),
            CreatedType,
            Guid.Empty)
        {
            ShortOverview = archivePath
        });
    }

    /// <summary>
    /// Records that a backup failed.
    /// </summary>
    /// <param name="activityManager">The activity manager.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="exception">What went wrong.</param>
    /// <returns>A task representing the write.</returns>
    public static Task LogFailedAsync(IActivityManager activityManager, ILocalizationManager localization, Exception exception)
    {
        return activityManager.CreateAsync(new ActivityLog(
            string.Format(
                CultureInfo.InvariantCulture,
                localization.GetServerLocalizedString("BackupFailedWithMessage"),
                exception.Message),
            FailedType,
            Guid.Empty)
        {
            LogSeverity = LogLevel.Error,
            Overview = exception.ToString()
        });
    }

    /// <summary>
    /// Formats a byte count with a binary unit and one decimal, e.g. <c>12.4 MiB</c>.
    /// </summary>
    /// <param name="bytes">The size in bytes.</param>
    /// <returns>The formatted size.</returns>
    public static string HumanizeSize(long bytes)
    {
        double value = Math.Max(bytes, 0);
        var place = 0;
        while (value >= 1024 && place < _sizeSuffixes.Length - 1)
        {
            value /= 1024;
            place++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {_sizeSuffixes[place]}");
    }
}
