namespace Jellyfin.Api.Models.FamilyDtos;

/// <summary>
/// The request body for switching to another profile of the same family.
/// </summary>
public class SwitchFamilyProfileDto
{
    /// <summary>
    /// Gets or sets the PIN or password of the profile to switch to; empty for an open profile.
    /// </summary>
    public string? Pw { get; set; }
}
