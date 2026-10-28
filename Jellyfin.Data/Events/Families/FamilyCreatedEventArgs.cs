using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a family is created.
/// </summary>
public class FamilyCreatedEventArgs : GenericEventArgs<Family>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyCreatedEventArgs"/> class.
    /// </summary>
    /// <param name="arg">The family.</param>
    public FamilyCreatedEventArgs(Family arg) : base(arg)
    {
    }
}
