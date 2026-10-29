using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.Models.FamilyDtos;
using Jellyfin.Data;
using Jellyfin.Data.Events.Families;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Playlists;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Families: groups of up to six profiles that only see each other. A server administrator creates
/// them and decides which libraries each family reaches; a member with the family administrator
/// permission manages the family's profiles without any access to the server dashboard.
/// </summary>
[Route("Families")]
[Authorize]
public class FamilyController : BaseJellyfinApiController
{
    private readonly IFamilyManager _familyManager;
    private readonly IUserManager _userManager;
    private readonly ISessionManager _sessionManager;
    private readonly IPlaylistManager _playlistManager;
    private readonly IAuthorizationContext _authContext;
    private readonly IEventManager _eventManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ImageUploadHelper _imageUploadHelper;
    private readonly IDeviceManager _deviceManager;
    private readonly IUserDataManager _userDataManager;
    private readonly IImageProcessor _imageProcessor;

    /// <summary>
    /// Initializes a new instance of the <see cref="FamilyController"/> class.
    /// </summary>
    /// <param name="familyManager">Instance of the <see cref="IFamilyManager"/> interface.</param>
    /// <param name="userManager">Instance of the <see cref="IUserManager"/> interface.</param>
    /// <param name="sessionManager">Instance of the <see cref="ISessionManager"/> interface.</param>
    /// <param name="playlistManager">Instance of the <see cref="IPlaylistManager"/> interface.</param>
    /// <param name="authContext">Instance of the <see cref="IAuthorizationContext"/> interface.</param>
    /// <param name="eventManager">Instance of the <see cref="IEventManager"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="imageUploadHelper">Instance of the <see cref="ImageUploadHelper"/> class.</param>
    /// <param name="deviceManager">Instance of the <see cref="IDeviceManager"/> interface.</param>
    /// <param name="userDataManager">Instance of the <see cref="IUserDataManager"/> interface.</param>
    /// <param name="imageProcessor">Instance of the <see cref="IImageProcessor"/> interface.</param>
    public FamilyController(
        IFamilyManager familyManager,
        IUserManager userManager,
        ISessionManager sessionManager,
        IPlaylistManager playlistManager,
        IAuthorizationContext authContext,
        IEventManager eventManager,
        ILibraryManager libraryManager,
        ImageUploadHelper imageUploadHelper,
        IDeviceManager deviceManager,
        IUserDataManager userDataManager,
        IImageProcessor imageProcessor)
    {
        _familyManager = familyManager;
        _userManager = userManager;
        _sessionManager = sessionManager;
        _playlistManager = playlistManager;
        _authContext = authContext;
        _eventManager = eventManager;
        _libraryManager = libraryManager;
        _imageUploadHelper = imageUploadHelper;
        _deviceManager = deviceManager;
        _userDataManager = userDataManager;
        _imageProcessor = imageProcessor;
    }

    private bool IsServerAdministrator => User.IsInRole(UserRoles.Administrator);

    /// <summary>
    /// Gets every family.
    /// </summary>
    /// <response code="200">Families returned.</response>
    /// <returns>The families.</returns>
    [HttpGet]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<FamilyDto>> GetFamilies()
        => Ok(_familyManager.GetFamilies().Select(ToDto));

    /// <summary>
    /// Creates a family that reaches every library.
    /// </summary>
    /// <param name="request">The family to create.</param>
    /// <response code="200">Family created.</response>
    /// <response code="400">The name is empty or already in use.</response>
    /// <returns>The new family.</returns>
    [HttpPost]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<FamilyDto>> CreateFamily([FromBody, Required] CreateFamilyDto request)
    {
        try
        {
            var family = await _familyManager.CreateFamilyAsync(request.Name).ConfigureAwait(false);
            return ToDto(family);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }
    }

    /// <summary>
    /// Gets the family of the current user.
    /// </summary>
    /// <response code="200">Family returned.</response>
    /// <response code="404">The current user does not belong to a family.</response>
    /// <returns>The family.</returns>
    [HttpGet("Mine")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FamilyDto> GetMyFamily()
    {
        var family = GetCaller()?.FamilyId is { } familyId ? _familyManager.GetFamily(familyId) : null;
        if (family is null)
        {
            return NotFound("The current user does not belong to a family.");
        }

        return ToDto(family);
    }

    /// <summary>
    /// Gets the libraries the current user's family reaches, by name, whether or not the current
    /// profile reaches them itself.
    /// </summary>
    /// <response code="200">Libraries returned.</response>
    /// <response code="404">The current user does not belong to a family.</response>
    /// <returns>The libraries.</returns>
    [HttpGet("Mine/Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<FamilyLibraryDto>> GetMyFamilyLibraries()
    {
        var family = GetCaller()?.FamilyId is { } familyId ? _familyManager.GetFamily(familyId) : null;
        if (family is null)
        {
            return NotFound("The current user does not belong to a family.");
        }

        return Ok(GetLibraries(family));
    }

    /// <summary>
    /// Gets a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <response code="200">Family returned.</response>
    /// <response code="403">The current user is not a member of the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The family.</returns>
    [HttpGet("{familyId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FamilyDto> GetFamily([FromRoute, Required] Guid familyId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see it.");
        }

        return ToDto(family);
    }

    /// <summary>
    /// Gets the libraries a family reaches, by name: every library of the server when the family
    /// reaches everything.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <response code="200">Libraries returned.</response>
    /// <response code="403">The current user is not a member of the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The libraries.</returns>
    [HttpGet("{familyId}/Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<FamilyLibraryDto>> GetFamilyLibraries([FromRoute, Required] Guid familyId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its libraries.");
        }

        return Ok(GetLibraries(family));
    }

    /// <summary>
    /// Updates a family: its name and, for a server administrator, the libraries it reaches. A field
    /// left out keeps its value. Changing the libraries caps every profile to the new reach.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="request">The new values.</param>
    /// <response code="204">Family updated.</response>
    /// <response code="400">The name is empty or already in use.</response>
    /// <response code="403">The current user does not manage the family, or is not a server administrator and sent the library fields.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{familyId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateFamily(
        [FromRoute, Required] Guid familyId,
        [FromBody, Required] UpdateFamilyDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can change the family.");
        }

        var changesLibraries = request.EnableAllFolders is not null || request.EnabledFolders is not null;
        if (changesLibraries && !IsServerAdministrator)
        {
            return Forbidden("Only a server administrator can change the libraries a family reaches.");
        }

        if (request.Name is not null)
        {
            try
            {
                await _familyManager.RenameFamilyAsync(family.Id, request.Name).ConfigureAwait(false);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
        }

        if (changesLibraries)
        {
            // A list without the flag means "only these"; the flag without a list keeps the stored list.
            var enableAllFolders = request.EnableAllFolders ?? false;
            var enabledFolders = request.EnabledFolders ?? family.GetEnabledFolderIds();
            await _familyManager.UpdateLibraryAccessAsync(family.Id, enableAllFolders, enabledFolders).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Deletes a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="deleteMembers">Whether to delete the family's profiles too. Otherwise they stay as users without a family.</param>
    /// <response code="204">Family deleted.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{familyId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteFamily(
        [FromRoute, Required] Guid familyId,
        [FromQuery] bool deleteMembers = false)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (deleteMembers)
        {
            foreach (var member in _familyManager.GetMembers(family.Id))
            {
                await DeleteUserCompletely(member.Id).ConfigureAwait(false);
                await _eventManager.PublishAsync(new FamilyProfileDeletedEventArgs(family, member)).ConfigureAwait(false);
            }
        }

        await _familyManager.DeleteFamilyAsync(family.Id).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Gets the profiles of a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <response code="200">Profiles returned.</response>
    /// <response code="403">The current user is not a member of the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The profiles. <c>HasPin</c> tells whether a profile asks for a PIN when switching to it.</returns>
    [HttpGet("{familyId}/Members")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IEnumerable<FamilyMemberDto>> GetMembers([FromRoute, Required] Guid familyId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its profiles.");
        }

        return Ok(_familyManager.GetMembers(family.Id).Select(member => ToMemberDto(member, family)));
    }

    /// <summary>
    /// Gets a profile of a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <response code="200">Profile returned.</response>
    /// <response code="403">The current user is not a member of the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>The profile.</returns>
    [HttpGet("{familyId}/Members/{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<FamilyMemberDto> GetMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        return ToMemberDto(member, family);
    }

    /// <summary>
    /// Adds a profile to a family. The profile is locked out for a while after a few wrong PINs; a
    /// family administrator may unlock it earlier.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="request">The profile to create.</param>
    /// <response code="200">Profile created.</response>
    /// <response code="400">The family is full or the name is invalid or taken.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The new profile.</returns>
    [HttpPost("{familyId}/Members")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FamilyMemberDto>> CreateMember(
        [FromRoute, Required] Guid familyId,
        [FromBody, Required] CreateFamilyMemberDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can add profiles.");
        }

        if (_familyManager.GetMembers(family.Id).Count >= _familyManager.MaxMembers)
        {
            return BadRequest(string.Format(
                CultureInfo.InvariantCulture,
                "The family already has {0} profiles, which is the maximum.",
                _familyManager.MaxMembers));
        }

        if (request.IsChildProfile && request.IsFamilyAdministrator)
        {
            return BadRequest("A child profile cannot be a family administrator.");
        }

        User newUser;
        try
        {
            newUser = await _userManager.CreateUserAsync(request.Name).ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        if (!string.IsNullOrEmpty(request.Password))
        {
            await _userManager.ChangePassword(newUser.Id, request.Password).ConfigureAwait(false);
        }

        await _familyManager.AssignUserAsync(newUser.Id, family.Id).ConfigureAwait(false);

        // A profile starts with the plain defaults: it is listed on the family's devices, cannot
        // touch the library's content, locks out after a few wrong PINs and only reaches what its
        // family and its creator reach.
        var policy = _userManager.GetUserDto(_userManager.GetUserById(newUser.Id)!).Policy;
        policy.IsHidden = false;
        policy.IsFamilyAdministrator = request.IsFamilyAdministrator;
        policy.IsChildProfile = request.IsChildProfile;
        policy.MaxParentalRating = request.MaxParentalRating;
        policy.LoginAttemptsBeforeLockout = Family.LoginAttemptsBeforeLockout;
        policy.EnableContentDeletion = false;
        policy.EnableContentDeletionFromFolders = [];
        policy.EnableLiveTvManagement = false;
        policy.EnableCollectionManagement = false;
        policy.EnableSubtitleManagement = false;
        policy.EnableLyricManagement = false;
        policy.EnableRemoteControlOfOtherUsers = false;
        ApplyLibraryAccess(policy, request.EnableAllFolders, request.EnabledFolders, GetCaller(), family);
        await _userManager.UpdatePolicyAsync(newUser.Id, policy).ConfigureAwait(false);

        var created = _userManager.GetUserById(newUser.Id)!;
        await _eventManager.PublishAsync(new FamilyProfileCreatedEventArgs(family, created)).ConfigureAwait(false);
        return ToMemberDto(created, family);
    }

    /// <summary>
    /// Moves an existing user into a family. The user keeps its settings, capped to the libraries
    /// the family reaches.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The user id.</param>
    /// <response code="204">User moved into the family.</response>
    /// <response code="400">The family is full or the user is a server administrator.</response>
    /// <response code="404">Family or user not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("{familyId}/Members/{userId}")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AddExistingMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return NotFound("User not found.");
        }

        if (Nullable.Equals(user.FamilyId, family.Id))
        {
            return NoContent();
        }

        try
        {
            await _familyManager.AssignUserAsync(userId, familyId).ConfigureAwait(false);
        }
        catch (ArgumentException e)
        {
            return BadRequest(e.Message);
        }

        await _eventManager.PublishAsync(new FamilyProfileLinkedEventArgs(family, _userManager.GetUserById(userId)!)).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Updates a family profile. Enabling a profile also unlocks it after too many wrong PINs.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <param name="request">The new values.</param>
    /// <response code="200">Profile updated.</response>
    /// <response code="400">The name is taken, or the change would leave the family without an administrator.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>The updated profile.</returns>
    [HttpPost("{familyId}/Members/{userId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<FamilyMemberDto>> UpdateMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromBody, Required] UpdateFamilyMemberDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can change profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (request.IsChildProfile && request.IsFamilyAdministrator)
        {
            return BadRequest("A child profile cannot be a family administrator.");
        }

        var caller = GetCaller();
        if (!IsServerAdministrator)
        {
            if (!request.IsFamilyAdministrator
                && member.HasPermission(PermissionKind.IsFamilyAdministrator)
                && !_familyManager.GetMembers(family.Id).Any(m => !m.Id.Equals(member.Id) && m.HasPermission(PermissionKind.IsFamilyAdministrator)))
            {
                return BadRequest("The family needs at least one family administrator.");
            }

            if (request.IsDisabled && caller is not null && caller.Id.Equals(member.Id))
            {
                return BadRequest("You cannot disable your own profile.");
            }
        }

        if (!string.Equals(member.Username, request.Name, StringComparison.Ordinal))
        {
            try
            {
                await _userManager.RenameUser(member.Id, member.Username, request.Name).ConfigureAwait(false);
            }
            catch (ArgumentException e)
            {
                return BadRequest(e.Message);
            }
        }

        var policy = _userManager.GetUserDto(_userManager.GetUserById(member.Id)!).Policy;
        policy.IsFamilyAdministrator = request.IsFamilyAdministrator;
        policy.IsChildProfile = request.IsChildProfile;
        policy.IsDisabled = request.IsDisabled;
        policy.MaxParentalRating = request.MaxParentalRating;
        if (!request.IsDisabled)
        {
            // Enabling is also the unlock: the wrong PINs are forgotten.
            policy.InvalidLoginAttemptCount = 0;
        }

        ApplyLibraryAccess(policy, request.EnableAllFolders, request.EnabledFolders ?? [], caller, family);
        await _userManager.UpdatePolicyAsync(member.Id, policy).ConfigureAwait(false);

        if (request.IsDisabled)
        {
            await _sessionManager.RevokeUserTokens(member.Id, null).ConfigureAwait(false);
        }
        else
        {
            // ... and so is a lockout that is still running.
            await _familyManager.UnlockMemberAsync(member.Id).ConfigureAwait(false);
        }

        return ToMemberDto(_userManager.GetUserById(member.Id)!, family);
    }

    /// <summary>
    /// Ends a profile's PIN lockout early. Only the lockout and the count of wrong PINs are cleared;
    /// a disabled profile stays disabled.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <response code="204">Profile unlocked, or it was not locked out.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{familyId}/Members/{userId}/Unlock")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UnlockMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can unlock profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        await _familyManager.UnlockMemberAsync(member.Id).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Sets the image of a family profile from a base64-encoded body, like <c>POST /UserImage</c>.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <response code="204">Image updated.</response>
    /// <response code="400">The content type is not an image.</response>
    /// <response code="403">The current user neither manages the family nor is the profile.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{familyId}/Members/{userId}/Image")]
    [AcceptsImageFile]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> PostMemberImage(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (!CanManageProfile(family, member))
        {
            return Forbidden("Only a family administrator or the profile itself can change its image.");
        }

        return await _imageUploadHelper.SaveUserImageAsync(member, Request).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes the image of a family profile.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <response code="204">Image deleted, or there was none.</response>
    /// <response code="403">The current user neither manages the family nor is the profile.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{familyId}/Members/{userId}/Image")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> DeleteMemberImage(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (!CanManageProfile(family, member))
        {
            return Forbidden("Only a family administrator or the profile itself can change its image.");
        }

        await _imageUploadHelper.DeleteUserImageAsync(member).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Sets or clears the PIN of a family profile.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <param name="request">The new PIN, or a reset.</param>
    /// <response code="204">PIN updated; the profile's other sessions are signed out.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("{familyId}/Members/{userId}/Password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> UpdateMemberPassword(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromBody, Required] UpdateFamilyMemberPasswordDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can change a profile's PIN.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (request.ResetPassword)
        {
            await _userManager.ResetPassword(member.Id).ConfigureAwait(false);
        }
        else
        {
            await _userManager.ChangePassword(member.Id, request.NewPassword ?? string.Empty).ConfigureAwait(false);
        }

        await _sessionManager.RevokeUserTokens(member.Id, User.GetToken()).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Removes a profile from a family.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <param name="deleteUser">Whether to delete the user. A server administrator may pass false to keep it as a user without a family.</param>
    /// <response code="204">Profile removed.</response>
    /// <response code="400">A family administrator cannot remove their own profile or keep the user.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{familyId}/Members/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveMember(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromQuery] bool deleteUser = true)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can remove profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (!IsServerAdministrator)
        {
            if (!deleteUser)
            {
                return BadRequest("Only a server administrator can keep the user outside the family.");
            }

            var caller = GetCaller();
            if (caller is not null && caller.Id.Equals(member.Id))
            {
                return BadRequest("You cannot remove your own profile.");
            }
        }

        if (deleteUser)
        {
            await DeleteUserCompletely(member.Id).ConfigureAwait(false);
            await _eventManager.PublishAsync(new FamilyProfileDeletedEventArgs(family, member)).ConfigureAwait(false);
        }
        else
        {
            await _familyManager.AssignUserAsync(member.Id, null).ConfigureAwait(false);
            await _eventManager.PublishAsync(new FamilyProfileUnlinkedEventArgs(family, member)).ConfigureAwait(false);
        }

        return NoContent();
    }

    /// <summary>
    /// Switches the current session to another profile of the same family, like picking a profile
    /// on a shared screen. A wrong PIN counts as a failed login for that profile.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The user id of the profile to switch to.</param>
    /// <param name="request">The profile's PIN.</param>
    /// <response code="200">Switched; the result carries the new access token.</response>
    /// <response code="400">The profile is the current one.</response>
    /// <response code="401">Wrong PIN.</response>
    /// <response code="403">The current user is not a member of the family, or the profile is disabled or locked out.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>The new session.</returns>
    [HttpPost("{familyId}/Members/{userId}/Switch")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(AuthenticationRateLimit.PolicyName)]
    public async Task<ActionResult<AuthenticationResult>> SwitchProfile(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromBody, Required] SwitchFamilyProfileDto request)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        var caller = GetCaller();
        if (caller is null || !Nullable.Equals(caller.FamilyId, family.Id))
        {
            return Forbidden("Only members of the family can switch between its profiles.");
        }

        var target = GetMember(family, userId);
        if (target is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (target.Id.Equals(caller.Id))
        {
            return BadRequest("This is already the current profile.");
        }

        if (target.HasPermission(PermissionKind.IsDisabled))
        {
            return Forbidden("This profile is disabled.");
        }

        if (target.LockedOutUntil is { } lockedOutUntil && lockedOutUntil > DateTime.UtcNow)
        {
            return Forbidden(string.Format(CultureInfo.InvariantCulture, "This profile is locked out until {0:u}.", lockedOutUntil));
        }

        var auth = await _authContext.GetAuthorizationInfo(Request).ConfigureAwait(false);
        var result = await _sessionManager.AuthenticateNewSession(new AuthenticationRequest
        {
            App = auth.Client,
            AppVersion = auth.Version,
            DeviceId = auth.DeviceId,
            DeviceName = auth.Device,
            Password = request.Pw ?? string.Empty,
            RemoteEndPoint = HttpContext.GetNormalizedRemoteIP().ToString(),
            Username = target.Username
        }).ConfigureAwait(false);

        await _eventManager.PublishAsync(new FamilyProfileSwitchedEventArgs(family, caller, target)).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Gets what a family profile played most recently, newest first: the items the profile's user
    /// data knows about and that the profile still reaches.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="userId">The profile's user id.</param>
    /// <param name="limit">The maximum number of entries, up to 200.</param>
    /// <response code="200">History returned.</response>
    /// <response code="403">The current user is neither the profile nor a family administrator.</response>
    /// <response code="404">Family or profile not found.</response>
    /// <returns>The history, newest first.</returns>
    [HttpGet("{familyId}/Members/{userId}/History")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<FamilyHistoryEntryDto>> GetMemberHistory(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] Guid userId,
        [FromQuery] int limit = 50)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its profiles.");
        }

        var member = GetMember(family, userId);
        if (member is null)
        {
            return NotFound("Profile not found in this family.");
        }

        if (!CanManageProfile(family, member))
        {
            return Forbidden("Only a family administrator or the profile itself can see its history.");
        }

        limit = Math.Clamp(limit, 1, 200);
        var history = new List<FamilyHistoryEntryDto>(limit);

        // Items that were deleted, or that the profile no longer reaches, fall out; ask for a few
        // more rows than needed so that a handful of them does not shorten the list.
        foreach (var entry in _userDataManager.GetPlayHistory(member, limit * 2))
        {
            if (history.Count >= limit)
            {
                break;
            }

            var item = _libraryManager.GetItemById<BaseItem>(entry.ItemId);
            if (item is null || item.IsFolder || !item.IsVisibleStandalone(member))
            {
                continue;
            }

            history.Add(ToHistoryEntry(item, entry));
        }

        return history;
    }

    /// <summary>
    /// Gets the devices signed in as a profile of a family, most recently used first.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <response code="200">Devices returned.</response>
    /// <response code="403">The current user is not a member of the family.</response>
    /// <response code="404">Family not found.</response>
    /// <returns>The devices. <c>IsCurrent</c> marks the one the caller is using.</returns>
    [HttpGet("{familyId}/Devices")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<IReadOnlyList<FamilyDeviceDto>> GetFamilyDevices([FromRoute, Required] Guid familyId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanView(family))
        {
            return Forbidden("Only members of the family can see its devices.");
        }

        var currentDeviceId = User.GetDeviceId();
        var devices = new List<FamilyDeviceDto>();
        foreach (var member in _familyManager.GetMembers(family.Id))
        {
            // One row per token; a device that signed in as the same profile more than once is one device.
            var memberDevices = _deviceManager.GetDevices(new DeviceQuery { UserId = member.Id }).Items
                .GroupBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.MaxBy(device => device.DateLastActivity)!);
            foreach (var device in memberDevices)
            {
                devices.Add(new FamilyDeviceDto
                {
                    Id = device.DeviceId,
                    Name = device.DeviceName,
                    AppName = device.AppName,
                    AppVersion = device.AppVersion,
                    UserId = member.Id,
                    UserName = member.Username,
                    DateLastActivity = device.DateLastActivity,
                    IsCurrent = string.Equals(device.DeviceId, currentDeviceId, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        return devices.OrderByDescending(device => device.DateLastActivity).ToList();
    }

    /// <summary>
    /// Signs a device out of a family: every token the device holds for a profile of the family is
    /// revoked, so the device has to sign in again.
    /// </summary>
    /// <param name="familyId">The family id.</param>
    /// <param name="deviceId">The device id.</param>
    /// <response code="204">Device signed out.</response>
    /// <response code="400">The device is the one the current user is using.</response>
    /// <response code="403">The current user does not manage the family.</response>
    /// <response code="404">Family not found, or no profile of the family is signed in on the device.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("{familyId}/Devices/{deviceId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> RemoveFamilyDevice(
        [FromRoute, Required] Guid familyId,
        [FromRoute, Required] string deviceId)
    {
        var family = _familyManager.GetFamily(familyId);
        if (family is null)
        {
            return NotFound("Family not found.");
        }

        if (!CanManage(family))
        {
            return Forbidden("Only a family administrator can sign out the family's devices.");
        }

        var tokens = _familyManager.GetMembers(family.Id)
            .SelectMany(member => _deviceManager.GetDevices(new DeviceQuery { UserId = member.Id, DeviceId = deviceId }).Items)
            .ToList();
        if (tokens.Count == 0)
        {
            return NotFound("No profile of this family is signed in on that device.");
        }

        if (string.Equals(deviceId, User.GetDeviceId(), StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest("You cannot sign out the device you are using.");
        }

        foreach (var token in tokens)
        {
            await _sessionManager.Logout(token).ConfigureAwait(false);
        }

        var deviceName = tokens.MaxBy(token => token.DateLastActivity)!.DeviceName;
        await _eventManager.PublishAsync(new FamilyDeviceRemovedEventArgs(family, deviceId, deviceName, GetCaller())).ConfigureAwait(false);
        return NoContent();
    }

    private FamilyHistoryEntryDto ToHistoryEntry(BaseItem item, PlayHistoryEntry entry)
    {
        var dto = new FamilyHistoryEntryDto
        {
            ItemId = item.Id,
            Name = item.Name,
            Type = item.GetBaseItemKind(),
            Date = entry.LastPlayedDate,
            Completed = entry.Played
        };

        if (!entry.Played && entry.PlaybackPositionTicks > 0 && item.RunTimeTicks is > 0)
        {
            dto.PlayedPercentage = 100.0 * entry.PlaybackPositionTicks / item.RunTimeTicks.Value;
        }

        BaseItem imageSource = item;
        if (item is Episode episode)
        {
            dto.SeriesName = episode.SeriesName;
            dto.SeasonName = episode.SeasonName;
            if (episode.Series is { } series)
            {
                imageSource = series;
            }
        }

        var image = imageSource.GetImageInfo(ImageType.Primary, 0);
        dto.ImageTag = image is null ? null : _imageProcessor.GetImageCacheTag(imageSource, image);
        return dto;
    }

    /// <summary>
    /// Limits a profile's library access to what the family reaches and, for a family administrator,
    /// to what they reach themselves. A server administrator (or an API key) hands out the whole
    /// family reach. "All folders" means every library of the family.
    /// </summary>
    private static void ApplyLibraryAccess(UserPolicy policy, bool? enableAllFolders, Guid[]? enabledFolders, User? manager, Family family)
    {
        // What the manager can reach: null means everything.
        Guid[]? managerReach = manager is null
            || manager.HasPermission(PermissionKind.IsAdministrator)
            || manager.HasPermission(PermissionKind.EnableAllFolders)
            ? null
            : manager.GetPreferenceValues<Guid>(PreferenceKind.EnabledFolders);

        // What can be granted: the family's reach, narrowed by the manager's own.
        var reach = Intersect(family.GetLibraryReach(), managerReach);

        bool all;
        Guid[] folders;
        if (enableAllFolders is null && enabledFolders is null)
        {
            // Nothing requested: the profile gets everything the manager can grant.
            all = reach is null;
            folders = reach ?? [];
        }
        else
        {
            all = enableAllFolders ?? false;
            folders = enabledFolders ?? [];
        }

        if (reach is not null)
        {
            if (all)
            {
                all = false;
                folders = reach;
            }
            else
            {
                folders = folders.Intersect(reach).ToArray();
            }
        }

        policy.EnableAllFolders = all;
        policy.EnabledFolders = folders;
    }

    /// <summary>
    /// Intersects two reaches, where <c>null</c> means everything.
    /// </summary>
    private static Guid[]? Intersect(Guid[]? first, Guid[]? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : first.Intersect(second).ToArray();
    }

    private User? GetCaller()
    {
        var userId = User.GetUserId();
        return userId.IsEmpty() ? null : _userManager.GetUserById(userId);
    }

    private bool CanView(Family family)
        => IsServerAdministrator || (GetCaller() is { } caller && Nullable.Equals(caller.FamilyId, family.Id));

    private bool CanManage(Family family)
        => IsServerAdministrator
            || (GetCaller() is { } caller
                && Nullable.Equals(caller.FamilyId, family.Id)
                && caller.HasPermission(PermissionKind.IsFamilyAdministrator));

    /// <summary>
    /// Whether the caller manages the family or is the profile itself.
    /// </summary>
    private bool CanManageProfile(Family family, User member)
        => CanManage(family) || (GetCaller() is { } caller && caller.Id.Equals(member.Id));

    /// <summary>
    /// Lists the libraries the family reaches, by name: every library of the server when the family
    /// reaches everything, otherwise the family's libraries that still exist.
    /// </summary>
    private IReadOnlyList<FamilyLibraryDto> GetLibraries(Family family)
    {
        var reach = family.GetLibraryReach();
        var libraries = new List<FamilyLibraryDto>();
        foreach (var folder in _libraryManager.GetVirtualFolders())
        {
            // A library whose folder has not been resolved yet has no id, so nothing can reach it.
            if (!Guid.TryParse(folder.ItemId, out var id) || id.IsEmpty())
            {
                continue;
            }

            if (reach is not null && !reach.Contains(id))
            {
                continue;
            }

            libraries.Add(new FamilyLibraryDto
            {
                Id = id,
                Name = folder.Name,
                CollectionType = folder.CollectionType
            });
        }

        return libraries;
    }

    private User? GetMember(Family family, Guid userId)
    {
        var user = _userManager.GetUserById(userId);
        return user is not null && Nullable.Equals(user.FamilyId, family.Id) ? user : null;
    }

    private ObjectResult Forbidden(string message)
        => StatusCode(StatusCodes.Status403Forbidden, message);

    private FamilyDto ToDto(Family family)
        => new()
        {
            Id = family.Id,
            Name = family.Name,
            DateCreated = family.DateCreated,
            MemberCount = _familyManager.GetMembers(family.Id).Count,
            MaxMembers = _familyManager.MaxMembers,
            EnableAllFolders = family.EnableAllFolders,
            EnabledFolders = family.GetEnabledFolderIds()
        };

    private FamilyMemberDto ToMemberDto(User user, Family family)
    {
        var source = _userManager.GetUserDto(user, HttpContext.GetNormalizedRemoteIP().ToString());
        var hasPin = !string.IsNullOrEmpty(user.Password);
        // A lockout that has run out is over, whatever the row still says until the next login.
        var lockedOutUntil = user.LockedOutUntil is { } until && until > DateTime.UtcNow ? until : (DateTime?)null;
        var dto = new FamilyMemberDto
        {
            Name = source.Name,
            ServerId = source.ServerId,
            ServerName = source.ServerName,
            Id = source.Id,
            FamilyId = source.FamilyId,
            PrimaryImageTag = source.PrimaryImageTag,
            EnableAutoLogin = source.EnableAutoLogin,
            LastLoginDate = source.LastLoginDate,
            LastActivityDate = source.LastActivityDate,
            Configuration = source.Configuration,
            Policy = source.Policy,
            PrimaryImageAspectRatio = source.PrimaryImageAspectRatio,
            HasPin = hasPin,
            // Profiles locked before the timed lockout existed were disabled with the attempts used up.
            IsLockedOut = lockedOutUntil.HasValue
                || (user.HasPermission(PermissionKind.IsDisabled)
                    && user.LoginAttemptsBeforeLockout is > 0
                    && user.InvalidLoginAttemptCount >= user.LoginAttemptsBeforeLockout),
            LockedOutUntil = lockedOutUntil,
            HasAllFamilyFolders = user.HasAllFamilyFolders(family)
        };
#pragma warning disable CS0618 // Older clients read the PIN flag from the obsolete properties; kept for one more release.
        dto.HasPassword = hasPin;
        dto.HasConfiguredPassword = hasPin;
#pragma warning restore CS0618
        return dto;
    }

    private async Task DeleteUserCompletely(Guid userId)
    {
        await _sessionManager.RevokeUserTokens(userId, null).ConfigureAwait(false);
        await _playlistManager.RemovePlaylistsAsync(userId).ConfigureAwait(false);
        await _userManager.DeleteUserAsync(userId).ConfigureAwait(false);
    }
}
