using System;
using System.Linq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;

namespace Jellyfin.Data;

/// <summary>
/// Library access rules between a <see cref="Family"/> and its members: a profile inherits the
/// family's reach and can never exceed it.
/// </summary>
public static class FamilyEntityExtensions
{
    /// <summary>
    /// Gets the libraries the family reaches.
    /// </summary>
    /// <param name="family">The family.</param>
    /// <returns><c>null</c> when the family reaches every library; otherwise the ids it reaches.</returns>
    public static Guid[]? GetLibraryReach(this Family family)
    {
        ArgumentNullException.ThrowIfNull(family);

        return family.EnableAllFolders ? null : family.GetEnabledFolderIds();
    }

    /// <summary>
    /// Caps the user's library access to what the family reaches, in memory. The caller persists the user.
    /// </summary>
    /// <remarks>
    /// When the family reaches every library nothing changes. Otherwise the user loses
    /// <c>EnableAllFolders</c>: a user that could reach everything now reaches exactly the family's
    /// libraries, and a user with a list keeps only the libraries the family also reaches.
    /// </remarks>
    /// <param name="user">The user.</param>
    /// <param name="family">The user's family.</param>
    /// <returns><c>true</c> when the user's access changed.</returns>
    public static bool CapToFamily(this User user, Family family)
    {
        ArgumentNullException.ThrowIfNull(user);

        var reach = family.GetLibraryReach();
        if (reach is null)
        {
            return false;
        }

        var current = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
        Guid[] capped = user.HasPermission(PermissionKind.EnableAllFolders)
            ? reach
            : current.Intersect(reach).ToArray();

        var changed = false;
        if (user.HasPermission(PermissionKind.EnableAllFolders))
        {
            user.SetPermission(PermissionKind.EnableAllFolders, false);
            changed = true;
        }

        if (!capped.SequenceEqual(current))
        {
            user.SetPreference(PreferenceKind.EnabledFolders, capped);
            changed = true;
        }

        return changed;
    }

    /// <summary>
    /// Checks whether the user's library access covers everything the family reaches.
    /// </summary>
    /// <param name="user">The user.</param>
    /// <param name="family">The user's family.</param>
    /// <returns><c>true</c> when the user reaches every library of the family.</returns>
    public static bool HasAllFamilyFolders(this User user, Family family)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (user.HasPermission(PermissionKind.EnableAllFolders))
        {
            return true;
        }

        var reach = family.GetLibraryReach();
        if (reach is null)
        {
            return false;
        }

        var enabled = user.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);
        return reach.All(enabled.Contains);
    }
}
