using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.SystemBackupService;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.SystemBackupService;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Backs up the database and the configuration on a schedule and keeps the last few archives it made.
/// </summary>
public class BackupTask : IScheduledTask, IConfigurableScheduledTask
{
    /// <summary>
    /// Prefix of the archives this task creates. The backup service names every archive
    /// "jellyfin-backup-{timestamp}.zip"; the task renames its own so the retention can tell them from
    /// backups made by hand through the API, which are never deleted here.
    /// </summary>
    public const string ScheduledBackupPrefix = "jellyfin-backup-scheduled-";

    /// <summary>
    /// How many archives made by this task are kept; the older ones are deleted after every run.
    /// </summary>
    public const int BackupsToKeep = 5;

    private readonly ILogger<BackupTask> _logger;
    private readonly ILocalizationManager _localization;
    private readonly IBackupService _backupService;
    private readonly IServerApplicationHost _applicationHost;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILibraryManager _libraryManager;
    private readonly IActivityManager _activityManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackupTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="backupService">Instance of the <see cref="IBackupService"/> interface.</param>
    /// <param name="applicationHost">Instance of the <see cref="IServerApplicationHost"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="activityManager">Instance of the <see cref="IActivityManager"/> interface.</param>
    public BackupTask(
        ILogger<BackupTask> logger,
        ILocalizationManager localization,
        IBackupService backupService,
        IServerApplicationHost applicationHost,
        IApplicationPaths applicationPaths,
        ILibraryManager libraryManager,
        IActivityManager activityManager)
    {
        _logger = logger;
        _localization = localization;
        _backupService = backupService;
        _applicationHost = applicationHost;
        _applicationPaths = applicationPaths;
        _libraryManager = libraryManager;
        _activityManager = activityManager;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskSystemBackup");

    /// <inheritdoc />
    public string Description => string.Format(
        CultureInfo.InvariantCulture,
        _localization.GetLocalizedString("TaskSystemBackupDescription"),
        BackupsToKeep);

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public string Key => "SystemBackup";

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.WeeklyTrigger,
            DayOfWeek = DayOfWeek.Sunday,
            TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A restore is applied on the next start, once this process has stopped: a backup taken now would
        // capture a system about to be replaced, and the retention must not touch the archive being restored.
        if (!string.IsNullOrEmpty(_applicationHost.RestoreBackupPath))
        {
            _logger.LogInformation("Skipping the scheduled backup, a restore from {Archive} is pending", _applicationHost.RestoreBackupPath);
            return;
        }

        // The backup service refuses to run during a scan (it would read an inconsistent database); the task
        // runs again on its next trigger, so skip instead of failing.
        if (_libraryManager.IsScanRunning)
        {
            _logger.LogInformation("Skipping the scheduled backup, a library scan is running");
            return;
        }

        progress.Report(0);

        string archivePath;
        try
        {
            // Database and configuration only: that is what cannot be rebuilt. Metadata, trickplay and subtitles
            // can be regenerated or downloaded again and would turn a few MB into many GB a week.
            var manifest = await _backupService.CreateBackupAsync(new BackupOptionsDto
            {
                Database = true,
                Metadata = false,
                Trickplay = false,
                Subtitles = false
            }).ConfigureAwait(false);
            progress.Report(80);

            archivePath = MarkAsScheduled(manifest);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The task framework records the failure too, but only the activity log is what an administrator
            // sees in the dashboard without opening the task.
            await BackupActivityLog.LogFailedAsync(_activityManager, _localization, ex).ConfigureAwait(false);
            throw;
        }

        await BackupActivityLog.LogCreatedAsync(_activityManager, _localization, archivePath).ConfigureAwait(false);
        progress.Report(90);

        await DeleteOldScheduledBackupsAsync(cancellationToken).ConfigureAwait(false);
        progress.Report(100);
    }

    /// <summary>
    /// Renames the archive with the scheduled prefix.
    /// </summary>
    /// <returns>The path of the archive after the rename, or its original path when the rename failed.</returns>
    private string MarkAsScheduled(BackupManifestDto manifest)
    {
        var directory = Path.GetDirectoryName(manifest.Path);
        if (string.IsNullOrEmpty(directory))
        {
            return manifest.Path;
        }

        // Same timestamp the service put in the original name, so the two names line up.
        var scheduledPath = Path.Combine(
            directory,
            ScheduledBackupPrefix + manifest.DateCreated.ToLocalTime().ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + ".zip");

        try
        {
            File.Move(manifest.Path, scheduledPath);
            _logger.LogInformation("Scheduled backup written to {Path}", scheduledPath);
            return scheduledPath;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The archive matters more than its name; it just stays out of the retention, like a manual backup.
            _logger.LogWarning(ex, "Could not rename {Path} to {ScheduledPath}, the archive keeps its original name", manifest.Path, scheduledPath);
            return manifest.Path;
        }
    }

    private async Task DeleteOldScheduledBackupsAsync(CancellationToken cancellationToken)
    {
        var backupPath = _applicationPaths.BackupPath;
        if (!Directory.Exists(backupPath))
        {
            return;
        }

        var scheduled = new List<(string Path, DateTimeOffset Created)>();
        foreach (var archive in Directory.EnumerateFiles(backupPath, ScheduledBackupPrefix + "*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Only archives the backup service recognizes count; a stray file with the prefix is left alone.
            var manifest = await _backupService.GetBackupManifest(archive).ConfigureAwait(false);
            if (manifest is not null)
            {
                scheduled.Add((archive, manifest.DateCreated));
            }
        }

        foreach (var (path, created) in scheduled.OrderByDescending(e => e.Created).Skip(BackupsToKeep))
        {
            try
            {
                File.Delete(path);
                _logger.LogInformation("Deleted the scheduled backup {Path} from {Date:u}, keeping the newest {Count}", path, created, BackupsToKeep);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete the old scheduled backup {Path}", path);
            }
        }
    }
}
