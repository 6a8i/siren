using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.FamilyDtos;

/// <summary>
/// The request body for creating a family.
/// </summary>
public class CreateFamilyDto
{
    /// <summary>
    /// Gets or sets the family name.
    /// </summary>
    [Required]
    public required string Name { get; set; }
}
