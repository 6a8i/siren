using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a profile is created in a family.
/// </summary>
public class FamilyProfileCreatedEventArgs : FamilyProfileEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileCreatedEventArgs"/> class.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="user">The new profile.</param>
    public FamilyProfileCreatedEventArgs(Family family, User user) : base(family, user)
    {
    }
}
