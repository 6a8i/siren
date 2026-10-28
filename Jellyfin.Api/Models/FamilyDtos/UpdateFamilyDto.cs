using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.FamilyDtos;

/// <summary>
/// The request body for renaming a family.
/// </summary>
public class UpdateFamilyDto
{
    /// <summary>
    /// Gets or sets the new family name.
    /// </summary>
    [Required]
    public required string Name { get; set; }
}
