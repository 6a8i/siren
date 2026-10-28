using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using Jellyfin.Database.Implementations.Interfaces;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A family: a group of up to <see cref="MaxMembers"/> user profiles that see only each other.
/// </summary>
/// <remarks>
/// Members are the users whose <see cref="User.FamilyId"/> points here; server administrators never
/// belong to a family. A member with the <c>IsFamilyAdministrator</c> permission manages the family's
/// profiles without any access to the server dashboard. The server administrator decides which
/// libraries the family reaches (<see cref="EnableAllFolders"/>, <see cref="EnabledFolders"/>); a
/// profile inherits that reach and can never exceed it.
/// </remarks>
public class Family : IHasConcurrencyToken
{
    /// <summary>
    /// The number of profiles a family can hold.
    /// </summary>
    public const int MaxMembers = 6;

    /// <summary>
    /// The number of wrong PINs a profile created through the Families API may enter before it is
    /// locked out for <see cref="LockoutDuration"/>.
    /// </summary>
    public const int LoginAttemptsBeforeLockout = 5;

    /// <summary>
    /// How long a family profile stays locked out after <see cref="LoginAttemptsBeforeLockout"/>
    /// wrong PINs (<see cref="User.LockedOutUntil"/>). A family administrator may unlock it earlier.
    /// </summary>
    public static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);

    /// <summary>
    /// The ids in <see cref="EnabledFolders"/> are separated by commas, which never appear in a Guid.
    /// </summary>
    private const char Delimiter = ',';

    /// <summary>
    /// Initializes a new instance of the <see cref="Family"/> class.
    /// </summary>
    /// <param name="name">The family name.</param>
    public Family(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Id = Guid.NewGuid();
        Name = name;
        DateCreated = DateTime.UtcNow;
        EnableAllFolders = true;
    }

    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the family name.
    /// </summary>
    /// <remarks>
    /// Required, Max length = 255.
    /// </remarks>
    [MaxLength(255)]
    [StringLength(255)]
    public string Name { get; set; }

    /// <summary>
    /// Gets or sets the date the family was created.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the family reaches every library. When <c>false</c>,
    /// the family reaches only <see cref="EnabledFolders"/>.
    /// </summary>
    public bool EnableAllFolders { get; set; }

    /// <summary>
    /// Gets or sets the ids of the libraries the family reaches when <see cref="EnableAllFolders"/>
    /// is <c>false</c>, as comma-separated Guids, or <c>null</c> for none. Use
    /// <see cref="GetEnabledFolderIds"/> and <see cref="SetEnabledFolderIds"/> rather than the raw value.
    /// </summary>
    /// <remarks>
    /// Max length = 65535.
    /// </remarks>
    [MaxLength(65535)]
    [StringLength(65535)]
    public string? EnabledFolders { get; set; }

    /// <inheritdoc />
    [ConcurrencyCheck]
    public uint RowVersion { get; private set; }

    /// <summary>
    /// Gets the ids of the libraries the family reaches when <see cref="EnableAllFolders"/> is <c>false</c>.
    /// </summary>
    /// <returns>The library ids; empty when none.</returns>
    public Guid[] GetEnabledFolderIds()
    {
        if (string.IsNullOrEmpty(EnabledFolders))
        {
            return [];
        }

        var parts = EnabledFolders.Split(Delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<Guid>(parts.Length);
        foreach (var part in parts)
        {
            if (Guid.TryParse(part, out var id) && !id.Equals(Guid.Empty))
            {
                ids.Add(id);
            }
        }

        return ids.ToArray();
    }

    /// <summary>
    /// Sets the ids of the libraries the family reaches when <see cref="EnableAllFolders"/> is <c>false</c>.
    /// Empty ids and duplicates are dropped.
    /// </summary>
    /// <param name="folderIds">The library ids.</param>
    public void SetEnabledFolderIds(IEnumerable<Guid> folderIds)
    {
        ArgumentNullException.ThrowIfNull(folderIds);

        var ids = folderIds
            .Where(id => !id.Equals(Guid.Empty))
            .Distinct()
            .Select(id => id.ToString("N", CultureInfo.InvariantCulture))
            .ToArray();

        EnabledFolders = ids.Length == 0 ? null : string.Join(Delimiter, ids);
    }

    /// <inheritdoc />
    public void OnSavingChanges()
    {
        RowVersion++;
    }
}
