using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when an existing user is moved into a family.
/// </summary>
public class FamilyProfileLinkedEventArgs : FamilyProfileEventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileLinkedEventArgs"/> class.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="user">The user that joined it.</param>
    public FamilyProfileLinkedEventArgs(Family family, User user) : base(family, user)
    {
    }
}
