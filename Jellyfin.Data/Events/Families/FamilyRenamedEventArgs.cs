using Jellyfin.Database.Implementations.Entities;

namespace Jellyfin.Data.Events.Families;

/// <summary>
/// An event that occurs when a family is renamed.
/// </summary>
public class FamilyRenamedEventArgs : GenericEventArgs<Family>
{
    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyRenamedEventArgs"/> class.
    /// </summary>
    /// <param name="arg">The family, carrying its new name.</param>
    /// <param name="previousName">The name the family had before.</param>
    public FamilyRenamedEventArgs(Family arg, string previousName) : base(arg)
    {
        PreviousName = previousName;
    }

    /// <summary>
    /// Gets the name the family had before.
    /// </summary>
    public string PreviousName { get; }
}
