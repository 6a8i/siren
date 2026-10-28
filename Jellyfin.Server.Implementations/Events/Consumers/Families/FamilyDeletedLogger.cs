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
/// Creates an entry in the activity log when a family is deleted.
/// </summary>
public class FamilyDeletedLogger : IEventConsumer<FamilyDeletedEventArgs>
{
    private readonly ILocalizationManager _localizationManager;
    private readonly IActivityManager _activityManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyDeletedLogger"/> class.
    /// </summary>
    /// <param name="localizationManager">The localization manager.</param>
    /// <param name="activityManager">The activity manager.</param>
    public FamilyDeletedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
    {
        _localizationManager = localizationManager;
        _activityManager = activityManager;
    }

    /// <inheritdoc />
    public async Task OnEvent(FamilyDeletedEventArgs eventArgs)
    {
        await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("FamilyDeletedWithName"),
                    eventArgs.Argument.Name),
                "FamilyDeleted",
                Guid.Empty))
            .ConfigureAwait(false);
    }
}
