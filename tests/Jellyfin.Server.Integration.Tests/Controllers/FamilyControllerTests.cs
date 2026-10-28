using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.FamilyDtos;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Dto;
using Xunit;
using Xunit.v3.Priority;

namespace Jellyfin.Server.Integration.Tests.Controllers;

/// <summary>
/// Drives a family through its life: created by the server administrator, managed by its family
/// administrator, with library access that never exceeds the family's and a PIN lockout that the
/// family administrator can undo. The tests run in priority order and share the state they build.
/// </summary>
[TestCaseOrderer(typeof(PriorityOrderer))]
public sealed class FamilyControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private const string FamilyName = "Silva";
    private const string FamilyAdminName = "familyAdmin";
    private const string FamilyAdminPin = "1234";
    private const string MemberName = "familyKid";
    private const string MemberPin = "0000";
    private const string WrongPin = "9999";

    private static readonly Guid _libraryA = Guid.NewGuid();
    private static readonly Guid _libraryB = Guid.NewGuid();

    private static string? _adminToken;
    private static string? _familyAdminToken;
    private static Guid _familyId;
    private static Guid _familyAdminId;
    private static Guid _memberId;

    private readonly JellyfinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;

    public FamilyControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Priority(0)]
    public async Task CreateFamily_AsServerAdministrator_ReachesEveryLibrary()
    {
        var client = await CreateServerAdminClient();

        using var response = await client.PostAsJsonAsync("Families", new CreateFamilyDto { Name = FamilyName }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var family = await response.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);

        Assert.NotNull(family);
        Assert.Equal(FamilyName, family.Name);
        Assert.Equal(0, family.MemberCount);
        Assert.Equal(Family.MaxMembers, family.MaxMembers);
        Assert.True(family.EnableAllFolders);
        Assert.Empty(family.EnabledFolders);

        _familyId = family.Id;
    }

    [Fact]
    [Priority(1)]
    public async Task CreateMembers_FirstAdministratorAndProfileWithPin_LockOutAfterFiveAttempts()
    {
        var client = await CreateServerAdminClient();

        var familyAdmin = await CreateMember(client, new CreateFamilyMemberDto
        {
            Name = FamilyAdminName,
            Password = FamilyAdminPin,
            IsFamilyAdministrator = true
        });
        Assert.True(familyAdmin.Policy.IsFamilyAdministrator);
        Assert.False(familyAdmin.Policy.IsAdministrator);
        Assert.False(familyAdmin.Policy.IsHidden);
        _familyAdminId = familyAdmin.Id;

        var member = await CreateMember(client, new CreateFamilyMemberDto
        {
            Name = MemberName,
            Password = MemberPin
        });
        Assert.False(member.Policy.IsFamilyAdministrator);
        _memberId = member.Id;

        foreach (var profile in new[] { familyAdmin, member })
        {
            Assert.Equal(_familyId, profile.FamilyId);
            Assert.True(profile.HasPin);
            Assert.False(profile.IsLockedOut);
            Assert.True(profile.HasAllFamilyFolders);
            Assert.True(profile.Policy.EnableAllFolders);
            Assert.Equal(Family.LoginAttemptsBeforeLockout, profile.Policy.LoginAttemptsBeforeLockout);
        }

        var listed = await GetMembers(client);
        Assert.Equal(2, listed.Length);
        Assert.All(listed, profile => Assert.True(profile.HasPin));
    }

    [Fact]
    [Priority(2)]
    public async Task FamilyAdministrator_SeesOnlyTheFamily()
    {
        var client = await CreateFamilyAdminClient();

        using var mine = await client.GetAsync("Families/Mine", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, mine.StatusCode);
        var family = await mine.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(family);
        Assert.Equal(_familyId, family.Id);
        Assert.Equal(2, family.MemberCount);

        using var users = await client.GetAsync("Users", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, users.StatusCode);
        var visible = await users.Content.ReadFromJsonAsync<UserDto[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(visible);
        Assert.Equal(2, visible.Length);
        Assert.All(visible, user => Assert.Equal(_familyId, user.FamilyId));
        Assert.Contains(visible, user => user.Id.Equals(_familyAdminId));
        Assert.Contains(visible, user => user.Id.Equals(_memberId));

        using var all = await client.GetAsync("Families", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, all.StatusCode);
    }

    [Fact]
    [Priority(3)]
    public async Task Switch_WrongPinIsUnauthorized_RightPinOpensTheProfile()
    {
        var client = await CreateFamilyAdminClient();

        using var wrong = await Switch(client, _memberId, WrongPin);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        using var right = await Switch(client, _memberId, MemberPin);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        var session = await right.Content.ReadFromJsonAsync<SwitchResult>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(session);
        Assert.False(string.IsNullOrEmpty(session.AccessToken));
        Assert.Equal(_memberId, session.User.Id);
    }

    [Fact]
    [Priority(4)]
    public async Task LibraryAccess_ProfilesNeverExceedTheFamily()
    {
        var familyAdmin = await CreateFamilyAdminClient();
        var serverAdmin = await CreateServerAdminClient();

        // Only the server administrator decides what the family reaches; a family administrator may still rename.
        using var forbidden = await familyAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { EnabledFolders = [_libraryA] }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var renamed = await familyAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { Name = FamilyName + " renamed" }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);

        using var narrowed = await serverAdmin.PostAsJsonAsync($"Families/{_familyId}", new UpdateFamilyDto { EnableAllFolders = false, EnabledFolders = [_libraryA] }, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, narrowed.StatusCode);

        using var get = await serverAdmin.GetAsync($"Families/{_familyId}", TestContext.Current.CancellationToken);
        var family = await get.Content.ReadFromJsonAsync<FamilyDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(family);
        Assert.Equal(FamilyName + " renamed", family.Name);
        Assert.False(family.EnableAllFolders);
        Assert.Equal(new[] { _libraryA }, family.EnabledFolders);

        // Every existing profile was capped, the family administrator included.
        foreach (var userId in new[] { _familyAdminId, _memberId })
        {
            var profile = await GetMember(serverAdmin, userId);
            Assert.False(profile.Policy.EnableAllFolders);
            Assert.Equal(new[] { _libraryA }, profile.Policy.EnabledFolders);
            Assert.True(profile.HasAllFamilyFolders);
        }

        // "All folders" from a family administrator means every library of the family.
        var updated = await UpdateMember(familyAdmin, _memberId, new UpdateFamilyMemberDto { Name = MemberName, EnableAllFolders = true });
        Assert.False(updated.Policy.EnableAllFolders);
        Assert.Equal(new[] { _libraryA }, updated.Policy.EnabledFolders);
        Assert.True(updated.HasAllFamilyFolders);

        // A library outside the family is dropped.
        updated = await UpdateMember(familyAdmin, _memberId, new UpdateFamilyMemberDto { Name = MemberName, EnabledFolders = [_libraryA, _libraryB] });
        Assert.Equal(new[] { _libraryA }, updated.Policy.EnabledFolders);

        updated = await UpdateMember(familyAdmin, _memberId, new UpdateFamilyMemberDto { Name = MemberName, EnabledFolders = [_libraryB] });
        Assert.Empty(updated.Policy.EnabledFolders);
        Assert.False(updated.HasAllFamilyFolders);

        // The dashboard's generic user page cannot exceed the family either.
        using var userResponse = await serverAdmin.GetAsync($"Users/{_memberId}", TestContext.Current.CancellationToken);
        var user = await userResponse.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(user);
        user.Policy.EnableAllFolders = true;
        user.Policy.EnabledFolders = [_libraryA, _libraryB];
        using var policyResponse = await serverAdmin.PostAsJsonAsync($"Users/{_memberId}/Policy", user.Policy, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, policyResponse.StatusCode);

        var capped = await GetMember(familyAdmin, _memberId);
        Assert.False(capped.Policy.EnableAllFolders);
        Assert.Equal(new[] { _libraryA }, capped.Policy.EnabledFolders);
        Assert.True(capped.HasAllFamilyFolders);
    }

    [Fact]
    [Priority(5)]
    public async Task Lockout_AfterFiveWrongPins_UnlockedByEnablingTheProfile()
    {
        var client = await CreateFamilyAdminClient();

        for (var attempt = 0; attempt < Family.LoginAttemptsBeforeLockout; attempt++)
        {
            using var wrong = await Switch(client, _memberId, WrongPin);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // The profile is now disabled, so the switch is refused before any PIN is checked.
        using var lockedOut = await Switch(client, _memberId, MemberPin);
        Assert.Equal(HttpStatusCode.Forbidden, lockedOut.StatusCode);

        var locked = await GetMember(client, _memberId);
        Assert.True(locked.IsLockedOut);
        Assert.True(locked.Policy.IsDisabled);
        Assert.True(locked.Policy.InvalidLoginAttemptCount >= Family.LoginAttemptsBeforeLockout);

        var unlocked = await UpdateMember(client, _memberId, new UpdateFamilyMemberDto { Name = MemberName, IsDisabled = false, EnableAllFolders = true });
        Assert.False(unlocked.IsLockedOut);
        Assert.False(unlocked.Policy.IsDisabled);
        Assert.Equal(0, unlocked.Policy.InvalidLoginAttemptCount);

        using var right = await Switch(client, _memberId, MemberPin);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    private async Task<HttpClient> CreateServerAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_adminToken ??= await AuthHelper.CompleteStartupAsync(client));
        return client;
    }

    private async Task<HttpClient> CreateFamilyAdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_familyAdminToken ??= await Authenticate(client, FamilyAdminName, FamilyAdminPin));
        return client;
    }

    private async Task<string> Authenticate(HttpClient client, string username, string pw)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, AuthHelper.DummyAuthHeader);
        request.Content = JsonContent.Create(new AuthenticateUserByName { Username = username, Pw = pw }, options: _jsonOptions);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SwitchResult>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result.AccessToken;
    }

    private async Task<FamilyMemberDto> CreateMember(HttpClient client, CreateFamilyMemberDto request)
    {
        using var response = await client.PostAsJsonAsync($"Families/{_familyId}/Members", request, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        Assert.Equal(request.Name, member.Name);
        return member;
    }

    private async Task<FamilyMemberDto> UpdateMember(HttpClient client, Guid userId, UpdateFamilyMemberDto request)
    {
        using var response = await client.PostAsJsonAsync($"Families/{_familyId}/Members/{userId}", request, _jsonOptions, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        Assert.Equal(userId, member.Id);
        return member;
    }

    private async Task<FamilyMemberDto> GetMember(HttpClient client, Guid userId)
    {
        using var response = await client.GetAsync($"Families/{_familyId}/Members/{userId}", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var member = await response.Content.ReadFromJsonAsync<FamilyMemberDto>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(member);
        return member;
    }

    private async Task<FamilyMemberDto[]> GetMembers(HttpClient client)
    {
        using var response = await client.GetAsync($"Families/{_familyId}/Members", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var members = await response.Content.ReadFromJsonAsync<FamilyMemberDto[]>(_jsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(members);
        return members.OrderBy(member => member.Name, StringComparer.Ordinal).ToArray();
    }

    private Task<HttpResponseMessage> Switch(HttpClient client, Guid userId, string pin)
        => client.PostAsJsonAsync($"Families/{_familyId}/Members/{userId}/Switch", new SwitchFamilyProfileDto { Pw = pin }, _jsonOptions, TestContext.Current.CancellationToken);

    private sealed class SwitchResult
    {
        public UserDto User { get; set; } = new();

        public string AccessToken { get; set; } = string.Empty;
    }
}
