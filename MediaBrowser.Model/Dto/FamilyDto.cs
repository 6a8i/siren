using System;

namespace MediaBrowser.Model.Dto;

/// <summary>
/// A family: a group of up to <see cref="MaxMembers"/> profiles that only see each other.
/// </summary>
public class FamilyDto
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the date the family was created.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets the number of profiles in the family.
    /// </summary>
    public int MemberCount { get; set; }

    /// <summary>
    /// Gets or sets the number of profiles the family can hold.
    /// </summary>
    public int MaxMembers { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the family reaches every library. Profiles inherit
    /// the family's reach and never exceed it.
    /// </summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets the libraries the family reaches when <see cref="EnableAllFolders"/> is false.
    /// </summary>
    public Guid[] EnabledFolders { get; set; } = [];
}
