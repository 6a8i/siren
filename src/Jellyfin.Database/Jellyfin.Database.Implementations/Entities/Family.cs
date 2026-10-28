using System;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Database.Implementations.Interfaces;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A family: a group of up to <see cref="MaxMembers"/> user profiles that see only each other.
/// </summary>
/// <remarks>
/// Members are the users whose <see cref="User.FamilyId"/> points here; server administrators never
/// belong to a family. A member with the <c>IsFamilyAdministrator</c> permission manages the family's
/// profiles without any access to the server dashboard.
/// </remarks>
public class Family : IHasConcurrencyToken
{
    /// <summary>
    /// The number of profiles a family can hold.
    /// </summary>
    public const int MaxMembers = 6;

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

    /// <inheritdoc />
    [ConcurrencyCheck]
    public uint RowVersion { get; private set; }

    /// <inheritdoc />
    public void OnSavingChanges()
    {
        RowVersion++;
    }
}
