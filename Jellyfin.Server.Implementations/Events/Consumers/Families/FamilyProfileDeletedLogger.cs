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
/// Creates an entry in the activity log when a profile of a family is deleted.
/// </summary>
public class FamilyProfileDeletedLogger : IEventConsumer<FamilyProfileDeletedEventArgs>
{
    private readonly ILocalizationManager _localizationManager;
    private readonly IActivityManager _activityManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileDeletedLogger"/> class.
    /// </summary>
    /// <param name="localizationManager">The localization manager.</param>
    /// <param name="activityManager">The activity manager.</param>
    public FamilyProfileDeletedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
    {
        _localizationManager = localizationManager;
        _activityManager = activityManager;
    }

    /// <inheritdoc />
    public async Task OnEvent(FamilyProfileDeletedEventArgs eventArgs)
    {
        // The user row is gone, so the entry is not tied to it, like the user deleted entry.
        await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("FamilyProfileDeletedWithNames"),
                    eventArgs.User.Username,
                    eventArgs.Family.Name),
                "FamilyProfileDeleted",
                Guid.Empty))
            .ConfigureAwait(false);
    }
}
