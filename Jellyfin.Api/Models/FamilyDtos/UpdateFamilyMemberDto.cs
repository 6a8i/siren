#pragma warning disable CA1819 // Properties should not return arrays

using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.FamilyDtos;

/// <summary>
/// The request body for updating a family profile. Every field replaces the stored value.
/// </summary>
public class UpdateFamilyMemberDto
{
    /// <summary>
    /// Gets or sets the profile name.
    /// </summary>
    [Required]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile manages the family.
    /// </summary>
    public bool IsFamilyAdministrator { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile is a child profile, shown a simplified UI
    /// by the client. A child profile cannot be a family administrator.
    /// </summary>
    public bool IsChildProfile { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile is disabled. <c>false</c> also unlocks a
    /// profile that was locked out by wrong PINs.
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum parental rating score, or <c>null</c> for no limit.
    /// </summary>
    public int? MaxParentalRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile reaches every library of its family. A
    /// profile never reaches more than its family.
    /// </summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets the libraries the profile reaches when <see cref="EnableAllFolders"/> is false.
    /// Libraries outside the family's reach (or the editor's) are dropped.
    /// </summary>
    public Guid[]? EnabledFolders { get; set; }
}
