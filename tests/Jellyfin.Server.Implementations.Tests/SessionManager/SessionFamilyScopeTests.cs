using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SessionManager;

/// <summary>
/// Sessions are scoped by family: a family administrator (or a member allowed to control other users)
/// sees their family, nobody sees another family, and remote control never crosses a family.
/// </summary>
public class SessionFamilyScopeTests
{
    private static readonly Guid _familyA = Guid.NewGuid();
    private static readonly Guid _familyB = Guid.NewGuid();

    [Fact]
    public async Task GetSessions_FamilyAdministrator_SeesTheirFamilyAndNothingElse()
    {
        await using var harness = await Harness.CreateAsync();

        var seen = harness.VisibleUserIds(harness.FamilyAdmin);

        Assert.Contains(harness.FamilyAdmin.Id, seen);
        Assert.Contains(harness.FamilyMember.Id, seen);
        Assert.DoesNotContain(harness.OtherFamily.Id, seen);
        Assert.DoesNotContain(harness.Outsider.Id, seen);
        Assert.DoesNotContain(harness.Admin.Id, seen);
    }

    [Fact]
    public async Task GetSessions_FamilyMemberWithoutRights_SeesOnlyTheirOwn()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Equal(new[] { harness.FamilyMember.Id }, harness.VisibleUserIds(harness.FamilyMember));
    }

    [Fact]
    public async Task GetSessions_FamilyMemberWhoMayControlOthers_SeesTheirFamilyOnly()
    {
        await using var harness = await Harness.CreateAsync();
        harness.FamilyMember.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);

        var seen = harness.VisibleUserIds(harness.FamilyMember);

        Assert.Contains(harness.FamilyAdmin.Id, seen);
        Assert.Contains(harness.FamilyMember.Id, seen);
        Assert.DoesNotContain(harness.OtherFamily.Id, seen);
        Assert.DoesNotContain(harness.Outsider.Id, seen);
    }

    [Fact]
    public async Task GetSessions_UserOutsideAnyFamily_SeesOnlyTheirOwn()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Outsider.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);

        // Unchanged: outside a family the plain listing never grows beyond the user's own sessions.
        Assert.Equal(new[] { harness.Outsider.Id }, harness.VisibleUserIds(harness.Outsider));
    }

    [Fact]
    public async Task GetSessions_ServerAdministratorAndApiKey_SeeEverything()
    {
        await using var harness = await Harness.CreateAsync();
        var everyone = harness.Users.Select(u => u.Id).Order().ToList();

        Assert.Equal(everyone, harness.VisibleUserIds(harness.Admin).Order());
        Assert.Equal(everyone, harness.Manager.GetSessions(Guid.Empty, null, null, null, true).Select(s => s.UserId).Order());
    }

    [Fact]
    public async Task GetSessions_ControllableByUser_StaysInsideTheFamily()
    {
        await using var harness = await Harness.CreateAsync();
        harness.FamilyMember.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);

        var controllable = harness.Manager
            .GetSessions(harness.FamilyMember.Id, null, null, harness.FamilyMember.Id, false)
            .Select(s => s.UserId)
            .ToList();

        Assert.Contains(harness.FamilyAdmin.Id, controllable);
        Assert.Contains(harness.FamilyMember.Id, controllable);
        Assert.DoesNotContain(harness.OtherFamily.Id, controllable);
        Assert.DoesNotContain(harness.Outsider.Id, controllable);
    }

    [Fact]
    public async Task GetSessions_ControllableByUserOutsideAnyFamily_DoesNotReachFamilies()
    {
        await using var harness = await Harness.CreateAsync();
        harness.Outsider.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);

        var controllable = harness.Manager
            .GetSessions(harness.Outsider.Id, null, null, harness.Outsider.Id, false)
            .Select(s => s.UserId)
            .ToList();

        Assert.Contains(harness.Outsider.Id, controllable);
        Assert.Contains(harness.SecondOutsider.Id, controllable);
        Assert.DoesNotContain(harness.FamilyAdmin.Id, controllable);
        Assert.DoesNotContain(harness.OtherFamily.Id, controllable);
    }

    [Fact]
    public async Task CanControlSession_OwnSession_IsAllowed()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.True(harness.CanControl(harness.FamilyMember, harness.FamilyMember));
    }

    [Fact]
    public async Task CanControlSession_WithoutThePermission_IsRefusedEvenInsideTheFamily()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.False(harness.CanControl(harness.FamilyAdmin, harness.FamilyMember));
        Assert.False(harness.CanControl(harness.FamilyMember, harness.OtherFamily));
    }

    [Fact]
    public async Task CanControlSession_WithThePermission_ReachesTheFamilyOnly()
    {
        await using var harness = await Harness.CreateAsync();
        harness.FamilyAdmin.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);

        Assert.True(harness.CanControl(harness.FamilyAdmin, harness.FamilyMember));
        Assert.False(harness.CanControl(harness.FamilyAdmin, harness.OtherFamily));
        Assert.False(harness.CanControl(harness.FamilyAdmin, harness.Outsider));
    }

    [Fact]
    public async Task CanControlSession_ServerAdministratorAndApiKey_ReachEverySession()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.True(harness.CanControl(harness.Admin, harness.OtherFamily));
        Assert.True(harness.Manager.CanControlSession(Guid.Empty, true, harness.SessionOf(harness.OtherFamily).Id));
    }

    [Fact]
    public async Task CanControlSession_WithoutUserOrApiKey_IsRefused()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.False(harness.Manager.CanControlSession(Guid.Empty, false, harness.SessionOf(harness.OtherFamily).Id));
        Assert.False(harness.Manager.CanControlSession(Guid.NewGuid(), false, harness.SessionOf(harness.OtherFamily).Id));
    }

    [Fact]
    public async Task CanControlSession_UnknownSession_Throws()
    {
        await using var harness = await Harness.CreateAsync();

        Assert.Throws<ResourceNotFoundException>(() => harness.Manager.CanControlSession(harness.Admin.Id, false, "no-such-session"));
    }

    [Fact]
    public async Task SendMessageCommand_ToAnotherFamily_ThrowsSecurityException()
    {
        await using var harness = await Harness.CreateAsync();
        harness.FamilyAdmin.SetPermission(PermissionKind.EnableRemoteControlOfOtherUsers, true);
        var command = new MessageCommand { Header = "Hello", Text = "from family A" };

        await harness.Manager.SendMessageCommand(
            harness.SessionOf(harness.FamilyAdmin).Id,
            harness.SessionOf(harness.FamilyMember).Id,
            command,
            CancellationToken.None);

        await Assert.ThrowsAsync<SecurityException>(() => harness.Manager.SendMessageCommand(
            harness.SessionOf(harness.FamilyAdmin).Id,
            harness.SessionOf(harness.OtherFamily).Id,
            command,
            CancellationToken.None));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Mock<IUserManager> _userManager = new();
        private readonly Mock<IDeviceManager> _deviceManager = new();
        private readonly Dictionary<Guid, SessionInfo> _sessions = new();

        private Harness()
        {
            _deviceManager.Setup(d => d.CanAccessDevice(It.IsAny<User>(), It.IsAny<string>())).Returns(true);

            Manager = new Emby.Server.Implementations.Session.SessionManager(
                NullLogger<Emby.Server.Implementations.Session.SessionManager>.Instance,
                Mock.Of<IEventManager>(),
                Mock.Of<IUserDataManager>(),
                Mock.Of<IServerConfigurationManager>(),
                Mock.Of<ILibraryManager>(),
                _userManager.Object,
                Mock.Of<IMusicManager>(),
                Mock.Of<IDtoService>(),
                Mock.Of<IImageProcessor>(),
                Mock.Of<IServerApplicationHost>(),
                _deviceManager.Object,
                Mock.Of<IMediaSourceManager>(),
                Mock.Of<IHostApplicationLifetime>());

            FamilyAdmin = AddUser("alice", _familyA, PermissionKind.IsFamilyAdministrator);
            FamilyMember = AddUser("amy", _familyA);
            OtherFamily = AddUser("bob", _familyB);
            Outsider = AddUser("carol", null);
            SecondOutsider = AddUser("dave", null);
            Admin = AddUser("admin", null, PermissionKind.IsAdministrator, PermissionKind.EnableRemoteControlOfOtherUsers);
            Users = [FamilyAdmin, FamilyMember, OtherFamily, Outsider, SecondOutsider, Admin];
        }

        public Emby.Server.Implementations.Session.SessionManager Manager { get; }

        /// <summary>Gets the administrator of family A.</summary>
        public User FamilyAdmin { get; }

        /// <summary>Gets a plain member of family A.</summary>
        public User FamilyMember { get; }

        /// <summary>Gets a member of family B.</summary>
        public User OtherFamily { get; }

        /// <summary>Gets a user outside any family.</summary>
        public User Outsider { get; }

        /// <summary>Gets another user outside any family.</summary>
        public User SecondOutsider { get; }

        /// <summary>Gets the server administrator.</summary>
        public User Admin { get; }

        public IReadOnlyList<User> Users { get; }

        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            foreach (var user in harness.Users)
            {
                var session = await harness.Manager.LogSessionActivity("Jellyfin Web", "1.0.0", user.Username + "-device", "device_name", "127.0.0.1", user);

                // Make the session remote controllable so the controllableByUserId listing considers it.
                session.Capabilities = new ClientCapabilities { SupportsMediaControl = true };
                session.AddController(Mock.Of<ISessionController>(c => c.SupportsMediaControl == true));
                harness._sessions[user.Id] = session;
            }

            return harness;
        }

        public SessionInfo SessionOf(User user) => _sessions[user.Id];

        public List<Guid> VisibleUserIds(User caller)
            => Manager.GetSessions(caller.Id, null, null, null, false).Select(s => s.UserId).ToList();

        public bool CanControl(User caller, User target)
            => Manager.CanControlSession(caller.Id, false, SessionOf(target).Id);

        public ValueTask DisposeAsync() => Manager.DisposeAsync();

        private User AddUser(string name, Guid? familyId, params PermissionKind[] permissions)
        {
            var user = new User(name, "default", "default") { FamilyId = familyId };
            user.SetPermission(PermissionKind.EnableSharedDeviceControl, true);
            foreach (var permission in permissions)
            {
                user.SetPermission(permission, true);
            }

            _userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
            return user;
        }
    }
}
