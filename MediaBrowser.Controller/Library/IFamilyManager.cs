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
    /// Creates a family that reaches every library.
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
    /// Sets the libraries a family reaches and caps every member to them: a member that could reach
    /// everything now reaches exactly the family's libraries, and a member with a list keeps only the
    /// libraries the family also reaches.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="enableAllFolders">Whether the family reaches every library.</param>
    /// <param name="enabledFolders">The libraries the family reaches when <paramref name="enableAllFolders"/> is <c>false</c>.</param>
    /// <returns>A task.</returns>
    Task UpdateLibraryAccessAsync(Guid familyId, bool enableAllFolders, IReadOnlyList<Guid> enabledFolders);

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
    /// A user that joins a family is capped to the family's libraries; a user that leaves a family
    /// loses the family administrator permission.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <param name="familyId">The family id, or <c>null</c>.</param>
    /// <returns>A task.</returns>
    Task AssignUserAsync(Guid userId, Guid? familyId);

    /// <summary>
    /// Ends a profile's PIN lockout early: clears <see cref="User.LockedOutUntil"/> and the count of
    /// wrong PINs. Nothing else about the user changes; a disabled profile stays disabled.
    /// </summary>
    /// <param name="userId">The user id.</param>
    /// <returns>A task.</returns>
    Task UnlockMemberAsync(Guid userId);
}
