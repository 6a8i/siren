using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a family is deleted.
/// </summary>
public class FamilyDeletedEventArgs : GenericEventArgs<Family>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyDeletedEventArgs"/> class.
    /// </summary>
    /// <param name="arg">The family.</param>
    public FamilyDeletedEventArgs(Family arg) : base(arg)
    {
    }
}
