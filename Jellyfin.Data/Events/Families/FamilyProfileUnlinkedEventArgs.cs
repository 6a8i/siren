using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a profile is removed from a family but kept as a user.
/// </summary>
public class FamilyProfileUnlinkedEventArgs : FamilyProfileEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileUnlinkedEventArgs"/> class.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="user">The user that left it.</param>
    public FamilyProfileUnlinkedEventArgs(Family family, User user) : base(family, user)
    {
    }
}
