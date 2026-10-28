using System;

namespace Jellyfin.Api.Models.FamilyDtos;

/// <summary>
/// The request body for updating a family. A field left out keeps its stored value.
/// </summary>
public class UpdateFamilyDto
{
    /// <summary>
    /// Gets or sets the new family name, or <c>null</c> to keep it.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the family reaches every library. Only a server
    /// administrator may send it; sending <see cref="EnabledFolders"/> without it means <c>false</c>.
    /// </summary>
    public bool? EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets the libraries the family reaches when <see cref="EnableAllFolders"/> is false.
    /// Only a server administrator may send it. Every member is capped to the new reach.
    /// </summary>
    public Guid[]? EnabledFolders { get; set; }
}
