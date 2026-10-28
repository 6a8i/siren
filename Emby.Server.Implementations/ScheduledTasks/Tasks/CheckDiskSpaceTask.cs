using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.StorageHelpers;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.System;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Warns, once a day per drive, when the drive holding the data, cache, transcoding or log directory is
/// running out of space.
/// </summary>
public class CheckDiskSpaceTask : IScheduledTask, IConfigurableScheduledTask
{
    /// <summary>
    /// Activity log type of a warning.
    /// </summary>
    public const string ActivityType = "LowDiskSpace";

    private readonly ILogger<CheckDiskSpaceTask> _logger;
    private readonly ILocalizationManager _localization;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IServerApplicationPaths _applicationPaths;
    private readonly IActivityManager _activityManager;

    // The task manager keeps one instance per task, so the once-a-day memory lives here.
    private readonly LowDiskSpaceChecker _checker = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CheckDiskSpaceTask"/> class.
    /// </summary>
    /// <param name="logger">Instance of the <see cref="ILogger"/> interface.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IServerApplicationPaths"/> interface.</param>
    /// <param name="activityManager">Instance of the <see cref="IActivityManager"/> interface.</param>
    public CheckDiskSpaceTask(
        ILogger<CheckDiskSpaceTask> logger,
        ILocalizationManager localization,
        IServerConfigurationManager configurationManager,
        IServerApplicationPaths applicationPaths,
        IActivityManager activityManager)
    {
        _logger = logger;
        _localization = localization;
        _configurationManager = configurationManager;
        _applicationPaths = applicationPaths;
        _activityManager = activityManager;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskCheckDiskSpace");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskCheckDiskSpaceDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public string Key => "CheckDiskSpace";

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
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var thresholdGb = _configurationManager.Configuration.LowDiskSpaceWarningGb;
        if (thresholdGb <= 0)
        {
            _logger.LogDebug("The disk space check is disabled (LowDiskSpaceWarningGb is {Threshold})", thresholdGb);
            progress.Report(100);
            return;
        }

        var folders = new List<FolderStorageInfo>();
        foreach (var path in WatchedPaths())
        {
            var info = StorageHelper.GetFreeSpaceOf(path);
            if (info.FreeSpace < 0)
            {
                _logger.LogWarning("Could not measure the free space of {Path}, skipping it in the disk space check", path);
                continue;
            }

            folders.Add(info);
        }

        progress.Report(50);

        foreach (var warning in _checker.Evaluate(folders, thresholdGb, DateTime.UtcNow))
        {
            var paths = string.Join(", ", warning.Paths);
            _logger.LogWarning(
                "Low disk space on {Drive} holding {Paths}: {FreeGb} GB free, below the {ThresholdGb} GB threshold",
                warning.DeviceId,
                paths,
                warning.FreeGb,
                warning.ThresholdGb);

            var entry = new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localization.GetServerLocalizedString("LowDiskSpaceWithPathsAndFree"),
                    paths,
                    warning.FreeGb,
                    warning.ThresholdGb),
                ActivityType,
                Guid.Empty)
            {
                LogSeverity = LogLevel.Warning,
                ShortOverview = warning.DeviceId
            };

            await _activityManager.CreateAsync(entry).ConfigureAwait(false);
        }

        progress.Report(100);
    }

    private IEnumerable<string> WatchedPaths()
    {
        yield return _applicationPaths.DataPath;
        yield return _applicationPaths.CachePath;
        yield return _configurationManager.GetTranscodePath();
        yield return _applicationPaths.LogDirectoryPath;
    }
}
