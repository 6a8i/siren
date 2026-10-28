using System.Linq;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.ServerSetupApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Gives the family profiles created before the PIN lockout existed the same limit of failed attempts
/// that new profiles get, so a four-digit PIN cannot be guessed without end on any of them.
/// </summary>
#pragma warning disable CS0618 // Type or member is obsolete
[JellyfinMigration("2026-10-28T20:00:00", nameof(SetFamilyProfileLockout))]
[JellyfinMigrationBackup(JellyfinDb = true)]
#pragma warning restore CS0618 // Type or member is obsolete
internal class SetFamilyProfileLockout : IDatabaseMigrationRoutine
{
    private readonly IStartupLogger _logger;
    private readonly IDbContextFactory<JellyfinDbContext> _provider;

    /// <summary>
    /// Initializes a new instance of the <see cref="SetFamilyProfileLockout"/> class.
    /// </summary>
    /// <param name="provider">The database context factory.</param>
    /// <param name="logger">The startup logger.</param>
    public SetFamilyProfileLockout(IDbContextFactory<JellyfinDbContext> provider, IStartupLogger<SetFamilyProfileLockout> logger)
    {
        _provider = provider;
        _logger = logger;
    }

    /// <inheritdoc />
    public void Perform()
    {
        using var context = _provider.CreateDbContext();
        var updated = context.Users
            .Where(user => user.FamilyId != null && (user.LoginAttemptsBeforeLockout == null || user.LoginAttemptsBeforeLockout <= 0))
            .ExecuteUpdate(setters => setters.SetProperty(user => user.LoginAttemptsBeforeLockout, Family.LoginAttemptsBeforeLockout));

        _logger.LogInformation("Set the PIN lockout limit on {Count} family profiles that had none", updated);
    }
}
