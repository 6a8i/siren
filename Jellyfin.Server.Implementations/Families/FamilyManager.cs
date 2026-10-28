using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Events.Families;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Families;

/// <summary>
/// Manages families and the users that belong to them.
/// </summary>
/// <remarks>
/// Membership lives on the user row (<see cref="User.FamilyId"/>) and is written through the
/// <see cref="IUserManager"/>, so its in-memory user snapshot stays correct. The family rows are
/// cached here the same way: loaded on first use, dropped by every write.
/// </remarks>
public sealed class FamilyManager : IFamilyManager
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IUserManager _userManager;
    private readonly IEventManager _eventManager;
    private readonly ILogger<FamilyManager> _logger;

    private volatile IReadOnlyDictionary<Guid, Family>? _families;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyManager"/> class.
    /// </summary>
    /// <param name="dbProvider">The database provider.</param>
    /// <param name="userManager">The user manager.</param>
    /// <param name="eventManager">The event manager.</param>
    /// <param name="logger">The logger.</param>
    public FamilyManager(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IUserManager userManager,
        IEventManager eventManager,
        ILogger<FamilyManager> logger)
    {
        _dbProvider = dbProvider;
        _userManager = userManager;
        _eventManager = eventManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public int MaxMembers => Family.MaxMembers;

    /// <inheritdoc />
    public IReadOnlyList<Family> GetFamilies()
        => GetSnapshot().Values
            .OrderBy(family => family.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <inheritdoc />
    public Family? GetFamily(Guid familyId)
        => GetSnapshot().GetValueOrDefault(familyId);

    /// <inheritdoc />
    public IReadOnlyList<User> GetMembers(Guid familyId)
        => _userManager.GetUsers()
            .Where(user => Nullable.Equals(user.FamilyId, familyId))
            .OrderBy(user => user.Username, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <inheritdoc />
    public async Task<Family> CreateFamilyAsync(string name)
    {
        name = ValidateName(name);
        if (GetSnapshot().Values.Any(family => string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, "A family named '{0}' already exists.", name));
        }

        var newFamily = new Family(name);
        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            dbContext.Families.Add(newFamily);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        _families = null;
        _logger.LogInformation("Created family {Name} ({Id})", newFamily.Name, newFamily.Id);
        await _eventManager.PublishAsync(new FamilyCreatedEventArgs(newFamily)).ConfigureAwait(false);
        return newFamily;
    }

    /// <inheritdoc />
    public async Task RenameFamilyAsync(Guid familyId, string name)
    {
        name = ValidateName(name);
        if (GetSnapshot().Values.Any(family => !family.Id.Equals(familyId) && string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException(string.Format(CultureInfo.InvariantCulture, "A family named '{0}' already exists.", name));
        }

        string previousName;
        Family family;
        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            family = await dbContext.Families
                .FirstOrDefaultAsync(f => f.Id.Equals(familyId))
                .ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("Family not found.");

            previousName = family.Name;
            family.Name = name;
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        _families = null;
        if (!string.Equals(previousName, name, StringComparison.Ordinal))
        {
            await _eventManager.PublishAsync(new FamilyRenamedEventArgs(family, previousName)).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task UpdateLibraryAccessAsync(Guid familyId, bool enableAllFolders, IReadOnlyList<Guid> enabledFolders)
    {
        ArgumentNullException.ThrowIfNull(enabledFolders);

        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            var family = await dbContext.Families
                .FirstOrDefaultAsync(f => f.Id.Equals(familyId))
                .ConfigureAwait(false)
                ?? throw new ResourceNotFoundException("Family not found.");

            family.EnableAllFolders = enableAllFolders;
            family.SetEnabledFolderIds(enabledFolders);
            await dbContext.SaveChangesAsync().ConfigureAwait(false);
        }

        _families = null;

        // The members may have been granted libraries the family no longer reaches.
        var updated = GetFamily(familyId)!;
        foreach (var member in GetMembers(familyId))
        {
            if (member.CapToFamily(updated))
            {
                await _userManager.UpdateUserAsync(member).ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public async Task DeleteFamilyAsync(Guid familyId)
    {
        var family = GetFamily(familyId) ?? throw new ResourceNotFoundException("Family not found.");

        // Detach the members through the user manager so its snapshot follows; the database would
        // null the column on its own, but the cached users would not know.
        foreach (var member in GetMembers(familyId))
        {
            await AssignUserAsync(member.Id, null).ConfigureAwait(false);
        }

        var dbContext = await _dbProvider.CreateDbContextAsync().ConfigureAwait(false);
        await using (dbContext.ConfigureAwait(false))
        {
            await dbContext.Families
                .Where(f => f.Id.Equals(familyId))
                .ExecuteDeleteAsync()
                .ConfigureAwait(false);
        }

        _families = null;
        _logger.LogInformation("Deleted family {Name} ({Id})", family.Name, family.Id);
        await _eventManager.PublishAsync(new FamilyDeletedEventArgs(family)).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task AssignUserAsync(Guid userId, Guid? familyId)
    {
        var user = _userManager.GetUserById(userId) ?? throw new ResourceNotFoundException("User not found.");

        if (familyId is null)
        {
            if (user.FamilyId is null)
            {
                return;
            }

            user.FamilyId = null;
            user.SetPermission(PermissionKind.IsFamilyAdministrator, false);
            // Family profiles are listed on their family's devices; outside a family they go back to the
            // default for new users and stay off the public login list.
            user.SetPermission(PermissionKind.IsHidden, true);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
            return;
        }

        var family = GetFamily(familyId.Value) ?? throw new ResourceNotFoundException("Family not found.");
        if (Nullable.Equals(user.FamilyId, family.Id))
        {
            return;
        }

        if (user.HasPermission(PermissionKind.IsAdministrator))
        {
            throw new ArgumentException("A server administrator cannot belong to a family.");
        }

        if (GetMembers(family.Id).Count >= MaxMembers)
        {
            throw new ArgumentException(string.Format(
                CultureInfo.InvariantCulture,
                "The family '{0}' already has {1} profiles, which is the maximum.",
                family.Name,
                MaxMembers));
        }

        user.FamilyId = family.Id;
        user.CapToFamily(family);
        // A PIN is only as safe as the number of guesses it allows: a user joining a family gets the
        // family default unless the server administrator already chose a limit.
        if (user.LoginAttemptsBeforeLockout is null or <= 0)
        {
            user.LoginAttemptsBeforeLockout = Family.LoginAttemptsBeforeLockout;
        }

        await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
    }

    private static string ValidateName(string name)
    {
        name = (name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("The family name cannot be empty.");
        }

        if (name.Length > 255)
        {
            throw new ArgumentException("The family name cannot be longer than 255 characters.");
        }

        return name;
    }

    private IReadOnlyDictionary<Guid, Family> GetSnapshot()
    {
        var snapshot = _families;
        if (snapshot is not null)
        {
            return snapshot;
        }

        using var dbContext = _dbProvider.CreateDbContext();
        snapshot = dbContext.Families
            .AsNoTracking()
            .ToDictionary(family => family.Id);

        _families = snapshot;
        return snapshot;
    }
}
