using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;

namespace MediaBrowser.Controller.Library;

/// <summary>
/// Manages families: groups of up to <see cref="MaxMembers"/> user profiles that only see each other.
/// </summary>
public interface IFamilyManager
{
    /// <summary>
    /// Gets the number of profiles a family can hold.
    /// </summary>
    int MaxMembers { get; }

    /// <summary>
    /// Gets every family, ordered by name.
    /// </summary>
    /// <returns>The families.</returns>
    IReadOnlyList<Family> GetFamilies();

    /// <summary>
    /// Gets a family by id.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <returns>The family, or <c>null</c> when none has that id.</returns>
    Family? GetFamily(Guid familyId);

    /// <summary>
    /// Gets the users that belong to a family, ordered by name.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <returns>The members.</returns>
    IReadOnlyList<User> GetMembers(Guid familyId);

    /// <summary>
    /// Creates a family.
    /// </summary>
    /// <param name="name">The family name; must not be in use by another family.</param>
    /// <returns>The new family.</returns>
    Task<Family> CreateFamilyAsync(string name);

    /// <summary>
    /// Renames a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="name">The new name.</param>
    /// <returns>A task.</returns>
    Task RenameFamilyAsync(Guid familyId, string name);

    /// <summary>
    /// Deletes a family. Its members are kept as users without a family and lose the family
    /// administrator permission.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <returns>A task.</returns>
    Task DeleteFamilyAsync(Guid familyId);

    /// <summary>
    /// Moves a user into a family, or out of any family when <paramref name="familyId"/> is <c>null</c>.
    /// A server administrator cannot join a family, and a family cannot grow past <see cref="MaxMembers"/>.
    /// A user that leaves a family loses the family administrator permission.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="familyId">The family id, or <c>null</c>.</param>
    /// <returns>A task.</returns>
    Task AssignUserAsync(Guid userId, Guid? familyId);
}
