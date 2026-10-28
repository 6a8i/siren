using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
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
using MediaBrowser.Model.Configuration;
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
        configManager.Setup(x => x.Configuration).Returns(new ServerConfiguration());
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

        _familyManager = new FamilyManager(factory.Object, _userManager, new NoopEventManager(), NullLogger<FamilyManager>.Instance);
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

    [Fact]
    public void EnabledFolderIds_RoundTripThroughTheColumn_DroppingEmptyAndDuplicateIds()
    {
        var family = new Jellyfin.Database.Implementations.Entities.Family("Ids");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        Assert.True(family.EnableAllFolders);
        Assert.Null(family.GetLibraryReach());
        Assert.Empty(family.GetEnabledFolderIds());

        family.SetEnabledFolderIds([a, Guid.Empty, b, a]);
        Assert.Equal(new[] { a, b }, family.GetEnabledFolderIds());

        family.EnableAllFolders = false;
        Assert.Equal(new[] { a, b }, family.GetLibraryReach());

        family.SetEnabledFolderIds([]);
        Assert.Null(family.EnabledFolders);
        Assert.Empty(family.GetEnabledFolderIds());
    }

    [Fact]
    public async Task UpdateLibraryAccess_SavesTheFamilyAndCapsEveryMember()
    {
        var family = await _familyManager.CreateFamilyAsync("Capped");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var c = Guid.NewGuid();

        var everything = await _userManager.CreateUserAsync("everything");
        var listed = await _userManager.CreateUserAsync("listed");
        listed.SetPermission(PermissionKind.EnableAllFolders, false);
        listed.SetPreference(PreferenceKind.EnabledFolders, new[] { a, c });
        await _userManager.UpdateUserAsync(listed);
        await _familyManager.AssignUserAsync(everything.Id, family.Id);
        await _familyManager.AssignUserAsync(listed.Id, family.Id);

        await _familyManager.UpdateLibraryAccessAsync(family.Id, false, [a, b]);

        var stored = _familyManager.GetFamily(family.Id)!;
        Assert.False(stored.EnableAllFolders);
        Assert.Equal(new[] { a, b }, stored.GetEnabledFolderIds());

        // A member that reached everything now reaches exactly the family's libraries.
        var everythingAfter = _userManager.GetUserById(everything.Id)!;
        Assert.False(everythingAfter.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { a, b }, everythingAfter.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.True(everythingAfter.HasAllFamilyFolders(stored));

        // A member with a list keeps only what the family also reaches.
        var listedAfter = _userManager.GetUserById(listed.Id)!;
        Assert.False(listedAfter.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { a }, listedAfter.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.False(listedAfter.HasAllFamilyFolders(stored));

        // Opening the family up again changes nothing on the members.
        await _familyManager.UpdateLibraryAccessAsync(family.Id, true, []);
        Assert.True(_familyManager.GetFamily(family.Id)!.EnableAllFolders);
        Assert.False(_userManager.GetUserById(everything.Id)!.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { a, b }, _userManager.GetUserById(everything.Id)!.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
    }

    [Fact]
    public async Task AssignUser_CapsTheNewMemberToTheFamily()
    {
        var family = await _familyManager.CreateFamilyAsync("Narrow");
        var a = Guid.NewGuid();
        await _familyManager.UpdateLibraryAccessAsync(family.Id, false, [a]);

        var joiner = await _userManager.CreateUserAsync("joiner");
        Assert.True(joiner.HasPermission(PermissionKind.EnableAllFolders));

        await _familyManager.AssignUserAsync(joiner.Id, family.Id);

        var after = _userManager.GetUserById(joiner.Id)!;
        Assert.Equal(family.Id, after.FamilyId);
        Assert.False(after.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { a }, after.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
        Assert.Equal(Family.LoginAttemptsBeforeLockout, after.LoginAttemptsBeforeLockout);
    }

    [Fact]
    public async Task UpdatePolicy_OfAFamilyMember_NeverExceedsTheFamily()
    {
        var family = await _familyManager.CreateFamilyAsync("Dashboard");
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await _familyManager.UpdateLibraryAccessAsync(family.Id, false, [a]);
        var member = await _userManager.CreateUserAsync("dashboarded");
        await _familyManager.AssignUserAsync(member.Id, family.Id);

        // The generic user page asks for everything: the member gets the family's libraries.
        var policy = _userManager.GetUserDto(_userManager.GetUserById(member.Id)!).Policy;
        policy.EnableAllFolders = true;
        await _userManager.UpdatePolicyAsync(member.Id, policy);

        var after = _userManager.GetUserById(member.Id)!;
        Assert.False(after.HasPermission(PermissionKind.EnableAllFolders));
        Assert.Equal(new[] { a }, after.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        // A list is intersected with the family's.
        policy = _userManager.GetUserDto(after).Policy;
        policy.EnableAllFolders = false;
        policy.EnabledFolders = [a, b];
        await _userManager.UpdatePolicyAsync(member.Id, policy);

        Assert.Equal(new[] { a }, _userManager.GetUserById(member.Id)!.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));

        // Outside a family the policy is stored as sent.
        var loner = await _userManager.CreateUserAsync("loner");
        policy = _userManager.GetUserDto(loner).Policy;
        policy.EnableAllFolders = false;
        policy.EnabledFolders = [a, b];
        await _userManager.UpdatePolicyAsync(loner.Id, policy);

        Assert.Equal(new[] { a, b }, _userManager.GetUserById(loner.Id)!.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders));
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
