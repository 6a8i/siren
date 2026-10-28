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
/// Creates an entry in the activity log when a family is renamed.
/// </summary>
public class FamilyRenamedLogger : IEventConsumer<FamilyRenamedEventArgs>
{
    private readonly ILocalizationManager _localizationManager;
    private readonly IActivityManager _activityManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyRenamedLogger"/> class.
    /// </summary>
    /// <param name="localizationManager">The localization manager.</param>
    /// <param name="activityManager">The activity manager.</param>
    public FamilyRenamedLogger(ILocalizationManager localizationManager, IActivityManager activityManager)
    {
        _localizationManager = localizationManager;
        _activityManager = activityManager;
    }

    /// <inheritdoc />
    public async Task OnEvent(FamilyRenamedEventArgs eventArgs)
    {
        await _activityManager.CreateAsync(new ActivityLog(
                string.Format(
                    CultureInfo.InvariantCulture,
                    _localizationManager.GetServerLocalizedString("FamilyRenamedWithNames"),
                    eventArgs.PreviousName,
                    eventArgs.Argument.Name),
                "FamilyRenamed",
                Guid.Empty))
            .ConfigureAwait(false);
    }
}
