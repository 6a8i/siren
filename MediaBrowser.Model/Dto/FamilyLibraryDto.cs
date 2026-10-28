using System;
using MediaBrowser.Model.Entities;

namespace MediaBrowser.Model.Dto;

/// <summary>
/// A library a family reaches, by name, so that a family administrator can see every library of the
/// family even when their own profile does not reach it.
/// </summary>
public class FamilyLibraryDto
{
    /// <summary>
    /// Gets or sets the library id (the id of its collection folder).
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the library's collection type, or <c>null</c> for a mixed library.
    /// </summary>
    public CollectionTypeOptions? CollectionType { get; set; }
}
