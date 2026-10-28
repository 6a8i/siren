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
    /// Gets or sets a value indicating whether the profile is disabled.
    /// </summary>
    public bool IsDisabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum parental rating score, or <c>null</c> for no limit.
    /// </summary>
    public int? MaxParentalRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile can access every library.
    /// </summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets the libraries the profile can access when <see cref="EnableAllFolders"/> is false.
    /// </summary>
    public Guid[]? EnabledFolders { get; set; }
}
