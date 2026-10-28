using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Families;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Families;

public sealed class FamilyManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly UserManager _userManager;
    private readonly FamilyManager _familyManager;

    public FamilyManagerTests()
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
        var appHost = new Mock<IApplicationHost>();

        _userManager = new UserManager(
            factory.Object,
            new NoopEventManager(),
            new Mock<INetworkManager>().Object,
            appHost.Object,
            new Mock<IImageProcessor>().Object,
            NullLogger<UserManager>.Instance,
            configManager.Object,
            new IPasswordResetProvider[] { new DefaultPasswordResetProvider(configManager.Object, appHost.Object) },
            new IAuthenticationProvider[]
            {
                new DefaultAuthenticationProvider(NullLogger<DefaultAuthenticationProvider>.Instance, new Mock<ICryptoProvider>().Object),
                new InvalidAuthProvider()
            });

        _familyManager = new FamilyManager(factory.Object, _userManager, NullLogger<FamilyManager>.Instance);
    }

    public void Dispose()
    {
        _userManager.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task CreateFamily_TrimsName_RejectsEmptyAndDuplicateNames()
    {
        var family = await _familyManager.CreateFamilyAsync("  Silva  ");

        Assert.Equal("Silva", family.Name);
        Assert.Equal(family.Id, _familyManager.GetFamily(family.Id)!.Id);
        Assert.Single(_familyManager.GetFamilies());

        await Assert.ThrowsAsync<ArgumentException>(() => _familyManager.CreateFamilyAsync("   "));
        await Assert.ThrowsAsync<ArgumentException>(() => _familyManager.CreateFamilyAsync("silva"));
    }

    [Fact]
    public async Task AssignUser_HoldsAtMostSixMembers_AndNoServerAdministrator()
    {
        var family = await _familyManager.CreateFamilyAsync("Full house");

        for (var i = 0; i < Jellyfin.Database.Implementations.Entities.Family.MaxMembers; i++)
        {
            var member = await _userManager.CreateUserAsync($"member{i}");
            await _familyManager.AssignUserAsync(member.Id, family.Id);
        }

        Assert.Equal(6, _familyManager.GetMembers(family.Id).Count);

        var seventh = await _userManager.CreateUserAsync("seventh");
        await Assert.ThrowsAsync<ArgumentException>(() => _familyManager.AssignUserAsync(seventh.Id, family.Id));
        Assert.Null(_userManager.GetUserById(seventh.Id)!.FamilyId);

        var admin = await _userManager.CreateUserAsync("serveradmin");
        admin.SetPermission(PermissionKind.IsAdministrator, true);
        await _userManager.UpdateUserAsync(admin);
        var smallFamily = await _familyManager.CreateFamilyAsync("Small");
        await Assert.ThrowsAsync<ArgumentException>(() => _familyManager.AssignUserAsync(admin.Id, smallFamily.Id));

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _familyManager.AssignUserAsync(seventh.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task LeavingAFamily_DropsTheFamilyAdministratorPermission()
    {
        var family = await _familyManager.CreateFamilyAsync("Leavers");
        var manager = await _userManager.CreateUserAsync("manager");
        await _familyManager.AssignUserAsync(manager.Id, family.Id);

        var stored = _userManager.GetUserById(manager.Id)!;
        stored.SetPermission(PermissionKind.IsFamilyAdministrator, true);
        await _userManager.UpdateUserAsync(stored);
        Assert.True(_userManager.GetUserById(manager.Id)!.HasPermission(PermissionKind.IsFamilyAdministrator));

        await _familyManager.AssignUserAsync(manager.Id, null);

        var after = _userManager.GetUserById(manager.Id)!;
        Assert.Null(after.FamilyId);
        Assert.False(after.HasPermission(PermissionKind.IsFamilyAdministrator));
    }

    [Fact]
    public async Task DeleteFamily_KeepsMembersAsUsersWithoutFamily()
    {
        var family = await _familyManager.CreateFamilyAsync("Gone");
        var first = await _userManager.CreateUserAsync("first");
        var second = await _userManager.CreateUserAsync("second");
        await _familyManager.AssignUserAsync(first.Id, family.Id);
        await _familyManager.AssignUserAsync(second.Id, family.Id);

        await _familyManager.DeleteFamilyAsync(family.Id);

        Assert.Null(_familyManager.GetFamily(family.Id));
        Assert.Empty(_familyManager.GetMembers(family.Id));
        Assert.All(
            _userManager.GetUsers().Where(u => u.Id.Equals(first.Id) || u.Id.Equals(second.Id)),
            user => Assert.Null(user.FamilyId));

        using var ctx = CreateDbContext();
        Assert.Equal(0, await ctx.Families.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RenameFamily_RejectsAnotherFamilysName()
    {
        var one = await _familyManager.CreateFamilyAsync("One");
        await _familyManager.CreateFamilyAsync("Two");

        await Assert.ThrowsAsync<ArgumentException>(() => _familyManager.RenameFamilyAsync(one.Id, "two"));
        await _familyManager.RenameFamilyAsync(one.Id, "One renamed");

        Assert.Equal("One renamed", _familyManager.GetFamily(one.Id)!.Name);
    }

    private JellyfinDbContext CreateDbContext()
    {
        return new JellyfinDbContext(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed class NoopEventManager : IEventManager
    {
        public void Publish<T>(T eventArgs)
            where T : EventArgs
        {
        }

        public Task PublishAsync<T>(T eventArgs)
            where T : EventArgs
            => Task.CompletedTask;
    }
}
