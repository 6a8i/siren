using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.FamilyDtos;

/// <summary>
/// The request body for adding a profile to a family.
/// </summary>
public class CreateFamilyMemberDto
{
    /// <summary>
    /// Gets or sets the profile name; it is the user name and must be unique on the server.
    /// </summary>
    [Required]
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets the PIN or password; <c>null</c> or empty leaves the profile open.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile manages the family.
    /// </summary>
    public bool IsFamilyAdministrator { get; set; }

    /// <summary>
    /// Gets or sets the maximum parental rating score, or <c>null</c> for no limit.
    /// </summary>
    public int? MaxParentalRating { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile reaches every library of its family. When
    /// neither this nor <see cref="EnabledFolders"/> is given, the profile gets the access of whoever
    /// creates it, within the family. A profile never reaches more than its family.
    /// </summary>
    public bool? EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets the libraries the profile reaches when <see cref="EnableAllFolders"/> is false.
    /// Libraries outside the family's reach (or the creator's) are dropped.
    /// </summary>
    public Guid[]? EnabledFolders { get; set; }
}
