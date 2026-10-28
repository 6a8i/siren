using System;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// The base of the events about one profile of a family.
/// </summary>
public abstract class FamilyProfileEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileEventArgs"/> class.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="user">The profile.</param>
    protected FamilyProfileEventArgs(Family family, User user)
    {
        Family = family;
        User = user;
    }

    /// <summary>
    /// Gets the family.
    /// </summary>
    public Family Family { get; }

    /// <summary>
    /// Gets the profile.
    /// </summary>
    public User User { get; }
}
