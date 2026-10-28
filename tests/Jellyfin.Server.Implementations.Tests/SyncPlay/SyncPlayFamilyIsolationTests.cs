using System;
using System.Linq;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Controller.SyncPlay.Requests;
using MediaBrowser.Model.SyncPlay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using SyncPlayGroup = Emby.Server.Implementations.SyncPlay.Group;
using SyncPlayManager = Emby.Server.Implementations.SyncPlay.SyncPlayManager;

namespace Jellyfin.Server.Implementations.Tests.SyncPlay;

/// <summary>
/// A SyncPlay group stays inside the family of the user that created it.
/// </summary>
public class SyncPlayFamilyIsolationTests
{
    private static readonly Guid _familyA = Guid.NewGuid();
    private static readonly Guid _familyB = Guid.NewGuid();

    [Fact]
    public void CreateGroup_TakesTheFamilyOfTheCreator()
    {
        var harness = new Harness();

        Assert.Equal(_familyA, harness.CreateGroup(harness.AddUser("alice", _familyA)).FamilyId);
        Assert.Null(harness.CreateGroup(harness.AddUser("carol", null)).FamilyId);
    }

    [Fact]
    public void IsVisibleTo_MemberOfTheSameFamily_IsTrue()
    {
        var harness = new Harness();
        var creator = harness.AddUser("alice", _familyA);
        var sibling = harness.AddUser("amy", _familyA);

        var group = harness.CreateGroup(creator);

        Assert.True(group.IsVisibleTo(creator));
        Assert.True(group.IsVisibleTo(sibling));
    }

    [Fact]
    public void IsVisibleTo_UserOutsideTheFamily_IsFalse()
    {
        var harness = new Harness();
        var group = harness.CreateGroup(harness.AddUser("alice", _familyA));

        Assert.False(group.IsVisibleTo(harness.AddUser("bob", _familyB)));
        Assert.False(group.IsVisibleTo(harness.AddUser("carol", null)));
        Assert.False(group.IsVisibleTo(null!));
    }

    [Fact]
    public void IsVisibleTo_GroupOfAUserWithoutFamily_IsVisibleOnlyToUsersWithoutFamily()
    {
        var harness = new Harness();
        var group = harness.CreateGroup(harness.AddUser("carol", null));

        Assert.True(group.IsVisibleTo(harness.AddUser("dave", null)));
        Assert.False(group.IsVisibleTo(harness.AddUser("alice", _familyA)));
    }

    [Fact]
    public void ListGroups_OnlyListsTheGroupsOfTheCallersFamily()
    {
        var harness = new Harness();
        var groupA = harness.NewGroup(harness.AddUser("alice", _familyA), "alice-tv", "Family A");
        harness.NewGroup(harness.AddUser("bob", _familyB), "bob-tv", "Family B");
        var groupC = harness.NewGroup(harness.AddUser("carol", null), "carol-tv", "No family");

        var amy = harness.CreateSession(harness.AddUser("amy", _familyA), "amy-phone");
        Assert.Equal(new[] { groupA.GroupId }, harness.Manager.ListGroups(amy, new ListGroupsRequest()).Select(g => g.GroupId));

        var dave = harness.CreateSession(harness.AddUser("dave", null), "dave-phone");
        Assert.Equal(new[] { groupC.GroupId }, harness.Manager.ListGroups(dave, new ListGroupsRequest()).Select(g => g.GroupId));
    }

    [Fact]
    public void GetGroup_GroupOfAnotherFamily_IsNull()
    {
        var harness = new Harness();
        var groupA = harness.NewGroup(harness.AddUser("alice", _familyA), "alice-tv", "Family A");

        var amy = harness.CreateSession(harness.AddUser("amy", _familyA), "amy-phone");
        var bob = harness.CreateSession(harness.AddUser("bob", _familyB), "bob-tv");

        Assert.NotNull(harness.Manager.GetGroup(amy, groupA.GroupId));
        Assert.Null(harness.Manager.GetGroup(bob, groupA.GroupId));
    }

    [Fact]
    public void JoinGroup_GroupOfAnotherFamily_IsRefusedAsIfItDidNotExist()
    {
        var harness = new Harness();
        var alice = harness.AddUser("alice", _familyA);
        var aliceSession = harness.CreateSession(alice, "alice-tv");
        var groupA = harness.Manager.NewGroup(aliceSession, new NewGroupRequest("Family A"), CancellationToken.None);

        var bob = harness.AddUser("bob", _familyB);
        var bobSession = harness.CreateSession(bob, "bob-tv");
        harness.Manager.JoinGroup(bobSession, new JoinGroupRequest(groupA.GroupId), CancellationToken.None);

        Assert.False(harness.Manager.IsUserActive(bob.Id));
        Assert.Equal(new[] { alice.Username }, harness.Manager.GetGroup(aliceSession, groupA.GroupId).Participants);
        harness.SessionManager.Verify(
            m => m.SendSyncPlayGroupUpdate(bobSession.Id, It.IsAny<SyncPlayGroupDoesNotExistUpdate>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public void JoinGroup_GroupOfTheSameFamily_Succeeds()
    {
        var harness = new Harness();
        var alice = harness.AddUser("alice", _familyA);
        var aliceSession = harness.CreateSession(alice, "alice-tv");
        var groupA = harness.Manager.NewGroup(aliceSession, new NewGroupRequest("Family A"), CancellationToken.None);

        var amy = harness.AddUser("amy", _familyA);
        var amySession = harness.CreateSession(amy, "amy-phone");
        harness.Manager.JoinGroup(amySession, new JoinGroupRequest(groupA.GroupId), CancellationToken.None);

        Assert.True(harness.Manager.IsUserActive(amy.Id));
        Assert.Equal(
            new[] { alice.Username, amy.Username }.Order(),
            harness.Manager.GetGroup(amySession, groupA.GroupId).Participants.Order());
    }

    private sealed class Harness
    {
        private readonly Mock<IUserManager> _userManager = new();
        private readonly Mock<ILibraryManager> _libraryManager = new();

        public Harness()
        {
            var item = new Mock<BaseItem>();
            item.Setup(i => i.IsVisibleStandalone(It.IsAny<User>())).Returns(true);
            _libraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>())).Returns(item.Object);

            Manager = new SyncPlayManager(
                NullLoggerFactory.Instance,
                _userManager.Object,
                SessionManager.Object,
                _libraryManager.Object);
        }

        public Mock<ISessionManager> SessionManager { get; } = new();

        public SyncPlayManager Manager { get; }

        public User AddUser(string name, Guid? familyId)
        {
            var user = new User(name, "auth-provider", "pwdreset-provider") { FamilyId = familyId };
            _userManager.Setup(m => m.GetUserById(user.Id)).Returns(user);
            return user;
        }

        public SessionInfo CreateSession(User user, string id)
        {
            return new SessionInfo(SessionManager.Object, NullLogger.Instance)
            {
                Id = id,
                UserId = user.Id,
                UserName = user.Username
            };
        }

        public GroupInfoDto NewGroup(User creator, string sessionId, string groupName)
            => Manager.NewGroup(CreateSession(creator, sessionId), new NewGroupRequest(groupName), CancellationToken.None);

        public SyncPlayGroup CreateGroup(User creator)
        {
            var group = new SyncPlayGroup(NullLoggerFactory.Instance, _userManager.Object, SessionManager.Object, _libraryManager.Object);
            group.CreateGroup(CreateSession(creator, creator.Username + "-session"), new NewGroupRequest("group"), CancellationToken.None);
            return group;
        }
    }
}
