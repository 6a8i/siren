using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a profile of a family is deleted.
/// </summary>
public class FamilyProfileDeletedEventArgs : FamilyProfileEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileDeletedEventArgs"/> class.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="user">The deleted profile.</param>
    public FamilyProfileDeletedEventArgs(Family family, User user) : base(family, user)
    {
    }
}
