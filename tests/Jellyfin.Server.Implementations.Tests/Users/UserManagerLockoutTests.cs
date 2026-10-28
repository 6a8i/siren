using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Cryptography;
using Jellyfin.Data;
using Jellyfin.Data.Events.Users;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Families;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Users;

/// <summary>
/// The lockout after too many wrong PINs: a family profile is locked out for a while and comes
/// back on its own, a user outside a family is disabled as upstream does.
/// </summary>
public sealed class UserManagerLockoutTests : IDisposable
{
    private const string Pin = "1234";
    private const string WrongPin = "0000";
    private const string RemoteEndPoint = "192.168.1.10";

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly CountingEventManager _events = new();
    private readonly UserManager _userManager;
    private readonly FamilyManager _familyManager;

    public UserManagerLockoutTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var ctx = CreateDbContext();
        ctx.Database.EnsureCreated();

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateDbContext);

        var configManager = new Mock<IServerConfigurationManager>();
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.Setup(x => x.ProgramDataPath).Returns(Path.GetTempPath());
        configManager.Setup(x => x.ApplicationPaths).Returns(appPaths.Object);
        configManager.Setup(x => x.Configuration).Returns(new ServerConfiguration());
        var appHost = new Mock<IApplicationHost>();
        var networkManager = new Mock<INetworkManager>();
        networkManager.Setup(x => x.IsInLocalNetwork(It.IsAny<string>())).Returns(true);

        _userManager = new UserManager(
            factory.Object,
            _events,
            networkManager.Object,
            appHost.Object,
            new Mock<IImageProcessor>().Object,
            NullLogger<UserManager>.Instance,
            configManager.Object,
            new IPasswordResetProvider[] { new DefaultPasswordResetProvider(configManager.Object, appHost.Object) },
            new IAuthenticationProvider[]
            {
                new DefaultAuthenticationProvider(NullLogger<DefaultAuthenticationProvider>.Instance, new CryptographyProvider()),
                new InvalidAuthProvider()
            });

        _familyManager = new FamilyManager(factory.Object, _userManager, _events, NullLogger<FamilyManager>.Instance);
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task FamilyMember_AfterTheLimit_IsLockedOutForAWhile_NotDisabled()
    {
        var member = await CreateFamilyMemberAsync("kid");

        var before = DateTime.UtcNow;
        await FailToAuthenticateAsync(member.Username, Family.LoginAttemptsBeforeLockout);
        var after = DateTime.UtcNow;

        var locked = _userManager.GetUserById(member.Id)!;
        Assert.False(locked.HasPermission(PermissionKind.IsDisabled));
        Assert.NotNull(locked.LockedOutUntil);
        Assert.InRange(locked.LockedOutUntil.Value, before + Family.LockoutDuration, after + Family.LockoutDuration);
        Assert.Equal(0, locked.InvalidLoginAttemptCount);
        Assert.Equal(1, _events.LockedOut);

        // While the lockout runs the right PIN is refused before it is even checked, and a wrong
        // one does not count.
        await Assert.ThrowsAsync<SecurityException>(() => _userManager.AuthenticateUser(member.Username, Pin, RemoteEndPoint, true));
        await Assert.ThrowsAsync<SecurityException>(() => _userManager.AuthenticateUser(member.Username, WrongPin, RemoteEndPoint, true));
        Assert.Equal(0, _userManager.GetUserById(member.Id)!.InvalidLoginAttemptCount);
        Assert.Equal(1, _events.LockedOut);
    }

    [Fact]
    public async Task FamilyMember_OnceTheLockoutIsOver_LogsInAndTheLockoutIsForgotten()
    {
        var member = await CreateFamilyMemberAsync("patient");

        var stored = _userManager.GetUserById(member.Id)!;
        stored.LockedOutUntil = DateTime.UtcNow.AddMinutes(-1);
        await _userManager.UpdateUserAsync(stored);

        var authenticated = await _userManager.AuthenticateUser(member.Username, Pin, RemoteEndPoint, true);

        Assert.NotNull(authenticated);
        Assert.Null(authenticated.LockedOutUntil);
        Assert.Null(_userManager.GetUserById(member.Id)!.LockedOutUntil);
    }

    [Fact]
    public async Task UnlockMember_EndsTheLockoutEarly()
    {
        var member = await CreateFamilyMemberAsync("unlocked");
        await FailToAuthenticateAsync(member.Username, Family.LoginAttemptsBeforeLockout);
        Assert.NotNull(_userManager.GetUserById(member.Id)!.LockedOutUntil);

        await _familyManager.UnlockMemberAsync(member.Id);

        var unlocked = _userManager.GetUserById(member.Id)!;
        Assert.Null(unlocked.LockedOutUntil);
        Assert.Equal(0, unlocked.InvalidLoginAttemptCount);
        Assert.False(unlocked.HasPermission(PermissionKind.IsDisabled));
        Assert.NotNull(await _userManager.AuthenticateUser(member.Username, Pin, RemoteEndPoint, true));
    }

    [Fact]
    public async Task UserOutsideAFamily_AfterTheLimit_IsDisabledAsBefore()
    {
        var loner = await _userManager.CreateUserAsync("loner");
        await _userManager.ChangePassword(loner.Id, Pin);
        var stored = _userManager.GetUserById(loner.Id)!;
        stored.LoginAttemptsBeforeLockout = 2;
        await _userManager.UpdateUserAsync(stored);

        await FailToAuthenticateAsync(loner.Username, 2);

        var disabled = _userManager.GetUserById(loner.Id)!;
        Assert.True(disabled.HasPermission(PermissionKind.IsDisabled));
        Assert.Null(disabled.LockedOutUntil);
        Assert.True(disabled.InvalidLoginAttemptCount >= 2);
        Assert.Equal(1, _events.LockedOut);

        await Assert.ThrowsAsync<SecurityException>(() => _userManager.AuthenticateUser(loner.Username, Pin, RemoteEndPoint, true));
    }

    private async Task<User> CreateFamilyMemberAsync(string name)
    {
        var family = await _familyManager.CreateFamilyAsync("Family of " + name);
        var member = await _userManager.CreateUserAsync(name);
        await _userManager.ChangePassword(member.Id, Pin);
        await _familyManager.AssignUserAsync(member.Id, family.Id);

        var stored = _userManager.GetUserById(member.Id)!;
        Assert.Equal(Family.LoginAttemptsBeforeLockout, stored.LoginAttemptsBeforeLockout);
        return stored;
    }

    private async Task FailToAuthenticateAsync(string username, int times)
    {
        for (var attempt = 0; attempt < times; attempt++)
        {
            Assert.Null(await _userManager.AuthenticateUser(username, WrongPin, RemoteEndPoint, true));
        }
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed class CountingEventManager : IEventManager
    {
        public int LockedOut { get; private set; }

        public void Publish<T>(T eventArgs)
            where T : EventArgs
            => Count(eventArgs);

        public Task PublishAsync<T>(T eventArgs)
            where T : EventArgs
        {
            Count(eventArgs);
            return Task.CompletedTask;
        }

        private void Count<T>(T eventArgs)
        {
            if (eventArgs is UserLockedOutEventArgs)
            {
                LockedOut++;
            }
        }
    }
}
