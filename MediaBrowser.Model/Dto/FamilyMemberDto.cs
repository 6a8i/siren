using System;

namespace MediaBrowser.Model.Dto;

/// <summary>
/// A profile of a family: the user plus what the family screen needs to know about it.
/// </summary>
public class FamilyMemberDto : UserDto
{
    /// <summary>
    /// Gets or sets a value indicating whether the profile asks for a PIN (the user has a password).
    /// </summary>
    public bool HasPin { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile is locked out because of too many wrong
    /// PINs: either until <see cref="LockedOutUntil"/>, or disabled the way profiles were locked
    /// before the timed lockout existed. A family administrator unlocks it early.
    /// </summary>
    public bool IsLockedOut { get; set; }

    /// <summary>
    /// Gets or sets the UTC time the current lockout ends, or <c>null</c> when the profile is not
    /// locked out for a while.
    /// </summary>
    public DateTime? LockedOutUntil { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile reaches every library its family reaches.
    /// </summary>
    public bool HasAllFamilyFolders { get; set; }
}
