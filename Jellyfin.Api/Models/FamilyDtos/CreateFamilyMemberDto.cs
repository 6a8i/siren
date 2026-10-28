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
    /// Gets or sets a value indicating whether the profile can access every library. When neither this
    /// nor <see cref="EnabledFolders"/> is given, the profile gets the access of whoever creates it.
    /// </summary>
    public bool? EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets the libraries the profile can access when <see cref="EnableAllFolders"/> is false.
    /// </summary>
    public Guid[]? EnabledFolders { get; set; }
}
