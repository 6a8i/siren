using System.Globalization;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Families;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Events;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Globalization;

namespace Jellyfin.Server.Implementations.Events.Consumers.Families;

/// <summary>
/// Creates an entry in the activity log when a profile is created in a family.
/// </summary>
public class FamilyProfileCreatedLogger : IEventConsumer<FamilyProfileCreatedEventArgs>
{
    private readonly ILocalizationManager _localizationManager;
    private readonly IActivityManager _activityManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileCreatedLogger"/> class.
    /// </summary>
    /// <param name="localizationManager">The localization manager.</param>
    /// <param name="activityManager">The activity manager.</param>
    public FamilyProfileCreatedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
    {
        _localizationManager = localizationManager;
        _activityManager = activityManager;
    }

    /// <inheritdoc />
    public async Task OnEvent(FamilyProfileCreatedEventArgs eventArgs)
    {
        await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("FamilyProfileCreatedWithNames"),
                    eventArgs.User.Username,
                    eventArgs.Family.Name),
                "FamilyProfileCreated",
                eventArgs.User.Id))
            .ConfigureAwait(false);
    }
}
