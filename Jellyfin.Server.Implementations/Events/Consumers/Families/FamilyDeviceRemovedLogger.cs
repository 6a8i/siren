using System;
using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Families;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Server.Implementations.Events.Consumers.Families;

/// <summary>
/// Creates an entry in the activity log when a device is signed out of a family.
/// </summary>
public class FamilyDeviceRemovedLogger : IEventConsumer<FamilyDeviceRemovedEventArgs>
{
    private readonly ILocalizationManager _localizationManager;
    private readonly IActivityManager _activityManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyDeviceRemovedLogger"/> class.
    /// </summary>
    /// <param name="localizationManager">The localization manager.</param>
    /// <param name="activityManager">The activity manager.</param>
    public FamilyDeviceRemovedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
    {
        _localizationManager = localizationManager;
        _activityManager = activityManager;
    }

    /// <inheritdoc />
    public async Task OnEvent(FamilyDeviceRemovedEventArgs eventArgs)
    {
        await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("FamilyDeviceRemovedWithNames"),
                    eventArgs.DeviceName,
                    eventArgs.Family.Name,
                    eventArgs.RemovedBy?.Username ?? "API key"),
                "FamilyDeviceRemoved",
                eventArgs.RemovedBy?.Id ?? Guid.Empty))
            .ConfigureAwait(false);
    }
}
