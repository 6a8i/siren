using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// Signs out the devices (access tokens) that have not been used for longer than
/// <see cref="MediaBrowser.Model.Configuration.ServerConfiguration.InactiveSessionDays"/>, so that a
/// forgotten tablet or a browser on someone else's computer stops carrying a valid token forever.
/// API keys live in their own table and are never touched.
/// </summary>
public class ExpireInactiveSessionsTask : IScheduledTask, IConfigurableScheduledTask
{
    private readonly ILogger<ExpireInactiveSessionsTask> _logger;
    private readonly ILocalizationManager _localization;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IDeviceManager _deviceManager;
    private readonly ISessionManager _sessionManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ExpireInactiveSessionsTask"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="localization">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="configurationManager">Instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    public ExpireInactiveSessionsTask(
        ILogger<ExpireInactiveSessionsTask> logger,
        ILocalizationManager localization,
        IServerConfigurationManager configurationManager,
        IDeviceManager deviceManager,
        ISessionManager sessionManager)
    {
        _logger = logger;
        _localization = localization;
        _configurationManager = configurationManager;
        _deviceManager = deviceManager;
        _sessionManager = sessionManager;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskExpireInactiveSessions");

    /// <inheritdoc />
    public string Key => "ExpireInactiveSessions";

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskExpireInactiveSessionsDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksMaintenanceCategory");

    /// <inheritdoc />
    public bool IsHidden => false;

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public bool IsLogged => true;

    /// <summary>
    /// Picks the devices whose last activity is older than the cutoff.
    /// </summary>
    /// <param name="devices">The devices to look at.</param>
    /// <param name="inactiveDays">Days without activity after which a device expires; zero or less expires nothing.</param>
    /// <param name="now">The current time, in UTC.</param>
    /// <returns>The devices to sign out.</returns>
    public static IReadOnlyList<Device> SelectExpired(IEnumerable<Device> devices, int inactiveDays, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(devices);

        if (inactiveDays <= 0)
        {
            return [];
        }

        var cutoff = now.AddDays(-inactiveDays);
        return devices.Where(device => device.DateLastActivity < cutoff).ToList();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var inactiveDays = _configurationManager.Configuration.InactiveSessionDays;
        if (inactiveDays <= 0)
        {
            _logger.LogInformation("Session expiry is disabled (InactiveSessionDays = {Days}); nothing to do", inactiveDays);
            progress.Report(100);
            return;
        }

        // Only user tokens live in the device table; API keys are stored apart and are never expired.
        var expired = SelectExpired(_deviceManager.GetDevices(new DeviceQuery()).Items, inactiveDays, DateTime.UtcNow);
        var revoked = 0;
        foreach (var device in expired)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _sessionManager.Logout(device).ConfigureAwait(false);
            revoked++;
            progress.Report(100.0 * revoked / expired.Count);
        }

        _logger.LogInformation("Revoked {Count} access tokens unused for more than {Days} days", revoked, inactiveDays);
        progress.Report(100);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }
}
