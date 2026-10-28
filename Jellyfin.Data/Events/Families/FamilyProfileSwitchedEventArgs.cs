using System;
using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a session switches to another profile of the same family.
/// </summary>
public class FamilyProfileSwitchedEventArgs : EventArgs
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyProfileSwitchedEventArgs"/> class.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <param name="from">The profile that was signed in.</param>
    /// <param name="to">The profile the session switched to.</param>
    public FamilyProfileSwitchedEventArgs(Family family, User from, User to)
    {
        Family = family;
        From = from;
        To = to;
    }

    /// <summary>
    /// Gets the family.
    /// </summary>
    public Family Family { get; }

    /// <summary>
    /// Gets the profile that was signed in.
    /// </summary>
    public User From { get; }

    /// <summary>
    /// Gets the profile the session switched to.
    /// </summary>
    public User To { get; }
}
