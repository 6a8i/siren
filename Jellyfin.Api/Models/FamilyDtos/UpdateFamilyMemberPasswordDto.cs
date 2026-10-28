namespace Jellyfin.Api.Models.FamilyDtos;

/// <summary>
/// The request body for setting or clearing a family profile's PIN.
/// </summary>
public class UpdateFamilyMemberPasswordDto
{
    /// <summary>
    /// Gets or sets the new PIN or password.
    /// </summary>
    public string? NewPassword { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether to clear the PIN instead.
    /// </summary>
    public bool ResetPassword { get; set; }
}
