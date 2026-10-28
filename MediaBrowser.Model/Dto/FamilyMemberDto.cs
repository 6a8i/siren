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
    /// Gets or sets a value indicating whether the profile is disabled because of too many wrong
    /// PINs. A family administrator unlocks it by enabling the profile again.
    /// </summary>
    public bool IsLockedOut { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile reaches every library its family reaches.
    /// </summary>
    public bool HasAllFamilyFolders { get; set; }
}
