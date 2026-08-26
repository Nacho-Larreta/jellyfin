using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Models.ProfileSelectorsDtos;
using Jellyfin.Data.Queries;
using Jellyfin.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Profile selector controller.
/// </summary>
[Route("ProfileSelectors")]
[Tags("ProfileSelector")]
public class ProfileSelectorsController : BaseJellyfinApiController
{
    private const int MaxProfileSwitchPlaybackStopRequestSize = 16 * 1024;
    private readonly IProfileSelectorManager _profileSelectorManager;
    private readonly IProfileSwitchCoordinator _profileSwitchCoordinator;
    private readonly IDeviceManager _deviceManager;
    private readonly ISessionManager _sessionManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileSelectorsController"/> class.
    /// </summary>
    /// <param name="profileSelectorManager">The profile selector manager.</param>
    /// <param name="profileSwitchCoordinator">The durable profile switch coordinator.</param>
    /// <param name="deviceManager">The persisted authentication-device manager.</param>
    /// <param name="sessionManager">The session manager.</param>
    /// <param name="userManager">The user manager.</param>
    public ProfileSelectorsController(
        IProfileSelectorManager profileSelectorManager,
        IProfileSwitchCoordinator profileSwitchCoordinator,
        IDeviceManager deviceManager,
        ISessionManager sessionManager,
        IUserManager userManager)
    {
        _profileSelectorManager = profileSelectorManager;
        _profileSwitchCoordinator = profileSwitchCoordinator;
        _deviceManager = deviceManager;
        _sessionManager = sessionManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Gets the current selector available to the authenticated runtime context.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Selector returned.</response>
    /// <response code="404">Selector not configured for the current context.</response>
    /// <returns>The current selector.</returns>
    [HttpGet("Current")]
    [Authorize(Policy = Policies.IgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileSelectorDto>> GetCurrentSelector(CancellationToken cancellationToken)
    {
        var deviceId = User.GetDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return BadRequest("DeviceId missing from authenticated request.");
        }

        var selector = await _profileSelectorManager.GetCurrentSelectorAsync(User.GetUserId(), deviceId, false, cancellationToken).ConfigureAwait(false);
        if (selector is null)
        {
            return NotFound();
        }

        return selector;
    }

    /// <summary>
    /// Activates a profile and issues a runtime token for it.
    /// </summary>
    /// <param name="profileUserId">The target profile user id.</param>
    /// <param name="request">The optional PIN payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Profile activated.</response>
    /// <response code="403">Profile is not accessible or PIN is invalid.</response>
    /// <response code="409">PIN required.</response>
    /// <response code="423">Profile temporarily locked by retry policy.</response>
    /// <returns>The activated runtime token payload.</returns>
    [HttpPost("Current/Profiles/{profileUserId}/Activate")]
    [Authorize(Policy = Policies.IgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<ActionResult<ProfileActivationResult>> ActivateProfile(
        [FromRoute, Required] Guid profileUserId,
        [FromBody] ProfileActivationRequestDto? request,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _profileSelectorManager.ActivateProfileAsync(
                new ProfileActivationContext
                {
                    CurrentUserId = User.GetUserId(),
                    ProfileUserId = profileUserId,
                    DeviceId = User.GetDeviceId(),
                    DeviceName = User.GetDevice(),
                    Client = User.GetClient(),
                    Version = User.GetVersion(),
                    RemoteEndPoint = HttpContext.GetNormalizedRemoteIP().ToString(),
                    Pin = request?.Pin
                },
                cancellationToken).ConfigureAwait(false);

            return result;
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Validates and durably prepares a profile switch without changing active identity.
    /// </summary>
    /// <param name="switchId">The client-generated switch identifier.</param>
    /// <param name="request">The target and optional PIN.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The prepared durable switch state.</returns>
    [HttpPost("Current/Switches/{switchId}/Prepare")]
    [Authorize(Policy = Policies.IgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status423Locked)]
    public async Task<ActionResult<ProfileSwitchResult>> PrepareProfileSwitch(
        [FromRoute, Required] Guid switchId,
        [FromBody, Required] ProfileSwitchPrepareRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _profileSwitchCoordinator.PrepareAsync(
                new ProfileSwitchPrepareContext
                {
                    SwitchId = switchId,
                    TargetProfileUserId = request.TargetProfileUserId,
                    Pin = request.Pin,
                    RequestContext = CreateSwitchRequestContext()
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Revalidates and commits a prepared profile switch.
    /// </summary>
    /// <param name="switchId">The durable switch identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The committed state and target runtime authentication.</returns>
    [HttpPost("Current/Switches/{switchId}/Commit")]
    [Authorize(Policy = Policies.IgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status410Gone)]
    public async Task<ActionResult<ProfileSwitchResult>> CommitProfileSwitch(
        [FromRoute, Required] Guid switchId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _profileSwitchCoordinator.CommitAsync(
                switchId,
                CreateSwitchRequestContext(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Resolves a durable profile switch after response loss or restart.
    /// </summary>
    /// <param name="switchId">The durable switch identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The current durable switch state.</returns>
    [HttpGet("Current/Switches/{switchId}")]
    [Authorize(Policy = Policies.IgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileSwitchResult>> GetProfileSwitchStatus(
        [FromRoute, Required] Guid switchId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _profileSwitchCoordinator.GetStatusAsync(
                switchId,
                CreateSwitchRequestContext(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Aborts a prepared profile switch before the commit point.
    /// </summary>
    /// <param name="switchId">The durable switch identifier.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The aborted durable switch state.</returns>
    [HttpDelete("Current/Switches/{switchId}")]
    [Authorize(Policy = Policies.IgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ProfileSwitchResult>> AbortProfileSwitch(
        [FromRoute, Required] Guid switchId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _profileSwitchCoordinator.AbortAsync(
                switchId,
                CreateSwitchRequestContext(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Idempotently reports the captured old playback session before commit.
    /// </summary>
    /// <param name="switchId">The durable switch identifier.</param>
    /// <param name="request">The captured old-session stop report.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stable, classified stop-report outcome.</returns>
    [HttpPost("Current/Switches/{switchId}/PlaybackStopped")]
    [Authorize(Policy = Policies.IgnoreParentalControl)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status413PayloadTooLarge)]
    [RequestSizeLimit(MaxProfileSwitchPlaybackStopRequestSize)]
    public async Task<ActionResult<ProfileSwitchPlaybackStopResult>> ReportProfileSwitchPlaybackStopped(
        [FromRoute, Required] Guid switchId,
        [FromBody, Required] ProfileSwitchPlaybackStopRequestDto request,
        CancellationToken cancellationToken)
    {
        if (Request.ContentLength > MaxProfileSwitchPlaybackStopRequestSize)
        {
            return StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            var requestContext = CreateSwitchRequestContext();
            var playbackStopInfo = new PlaybackStopInfo
            {
                ItemId = request.ItemId,
                PlaySessionId = request.PlaySessionId,
                PositionTicks = request.PositionTicks,
                Failed = request.Failed,
                NextMediaType = request.NextMediaType
            };
            return await _profileSwitchCoordinator.ReportPlaybackStoppedAsync(
                switchId,
                requestContext,
                playbackStopInfo,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Gets the selector configuration owned by a specific user.
    /// </summary>
    /// <param name="ownerUserId">The owner user id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Selector returned.</response>
    /// <response code="404">Selector not configured.</response>
    /// <returns>The selector owned by the requested user.</returns>
    [HttpGet("/Users/{ownerUserId}/ProfileSelector")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProfileSelectorDto>> GetSelectorForOwner(
        [FromRoute, Required] Guid ownerUserId,
        CancellationToken cancellationToken)
    {
        var deviceId = User.GetDeviceId();
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            deviceId = "admin-dashboard";
        }

        var selector = await _profileSelectorManager.GetSelectorForOwnerAsync(ownerUserId, deviceId, true, cancellationToken).ConfigureAwait(false);
        if (selector is null)
        {
            return NotFound();
        }

        return selector;
    }

    /// <summary>
    /// Gets user ids that are used as secondary profile backing users.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Secondary profile user ids returned.</response>
    /// <returns>The secondary profile user ids.</returns>
    [HttpGet("SecondaryProfileUserIds")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<Guid>>> GetSecondaryProfileUserIds(CancellationToken cancellationToken)
    {
        var userIds = await _profileSelectorManager.GetSecondaryProfileUserIdsAsync(cancellationToken).ConfigureAwait(false);
        return Ok(userIds);
    }

    /// <summary>
    /// Creates or replaces the selector configuration owned by a specific user.
    /// </summary>
    /// <param name="ownerUserId">The owner user id.</param>
    /// <param name="request">The full replacement payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Selector updated.</response>
    /// <response code="400">Request invalid.</response>
    /// <returns>The updated selector.</returns>
    [HttpPut("/Users/{ownerUserId}/ProfileSelector")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<ProfileSelectorDto>> UpdateSelectorForOwner(
        [FromRoute, Required] Guid ownerUserId,
        [FromBody, Required] ProfileSelectorUpdateRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            var configuration = new ProfileSelectorConfiguration
            {
                IsEnabled = request.IsEnabled,
                AutoSelectSingleProfile = request.AutoSelectSingleProfile
            };

            foreach (var profile in request.Profiles)
            {
                configuration.Profiles.Add(new ProfileSelectorMemberConfiguration
                {
                    ProfileUserId = profile.ProfileUserId,
                    DisplayOrder = profile.DisplayOrder,
                    IsVisible = profile.IsVisible
                });
            }

            var selector = await _profileSelectorManager.UpdateSelectorAsync(
                ownerUserId,
                User.GetDeviceId() ?? "admin-dashboard",
                configuration,
                cancellationToken).ConfigureAwait(false);

            return selector;
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Creates or replaces a profile PIN.
    /// </summary>
    /// <param name="ownerUserId">The owner user id.</param>
    /// <param name="profileUserId">The profile user id.</param>
    /// <param name="request">The PIN payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">PIN updated.</response>
    /// <returns>A no-content response.</returns>
    [HttpPost("/Users/{ownerUserId}/ProfileSelector/Profiles/{profileUserId}/Pin")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> SetProfilePin(
        [FromRoute, Required] Guid ownerUserId,
        [FromRoute, Required] Guid profileUserId,
        [FromBody, Required] ProfilePinUpdateRequestDto request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _profileSelectorManager.SetProfilePinAsync(ownerUserId, profileUserId, request.Pin, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    /// <summary>
    /// Removes a profile PIN.
    /// </summary>
    /// <param name="ownerUserId">The owner user id.</param>
    /// <param name="profileUserId">The profile user id.</param>
    /// <param name="request">The optional current PIN payload.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">PIN removed.</response>
    /// <returns>A no-content response.</returns>
    [HttpDelete("/Users/{ownerUserId}/ProfileSelector/Profiles/{profileUserId}/Pin")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeleteProfilePin(
        [FromRoute, Required] Guid ownerUserId,
        [FromRoute, Required] Guid profileUserId,
        [FromBody] ProfilePinClearRequestDto? request,
        CancellationToken cancellationToken)
    {
        try
        {
            await _profileSelectorManager.ClearProfilePinAsync(ownerUserId, profileUserId, request?.Pin, cancellationToken).ConfigureAwait(false);
            return NoContent();
        }
        catch (ProfileSelectorException ex)
        {
            return CreateProfileSelectorError(ex);
        }
    }

    private ActionResult CreateProfileSelectorError(ProfileSelectorException exception)
    {
        var details = new ProblemDetails
        {
            Status = exception.StatusCode,
            Title = exception.ErrorCode,
            Detail = exception.Message
        };

        details.Extensions["code"] = exception.ErrorCode;
        return StatusCode(exception.StatusCode, details);
    }

    private ProfileSwitchRequestContext CreateSwitchRequestContext()
    {
        if (User.GetIsApiKey())
        {
            throw new ProfileSelectorException(
                StatusCodes.Status403Forbidden,
                "PROFILE_SWITCH_USER_CREDENTIAL_REQUIRED",
                "Profile switching requires a persisted user credential.");
        }

        var accessToken = User.GetToken();
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ProfileSelectorException(
                StatusCodes.Status403Forbidden,
                "PROFILE_SWITCH_CREDENTIAL_REQUIRED",
                "Profile switching requires a persisted bearer credential.");
        }

        var credentials = _deviceManager.GetDevices(
            new DeviceQuery
            {
                AccessToken = accessToken,
                Limit = 2
            }).Items;
        if (credentials.Count != 1)
        {
            throw new ProfileSelectorException(
                StatusCodes.Status403Forbidden,
                "PROFILE_SWITCH_CREDENTIAL_INVALID",
                "The authenticated credential is not available for profile switching.");
        }

        var credential = credentials[0];
        if (!credential.UserId.Equals(User.GetUserId()))
        {
            throw new ProfileSelectorException(
                StatusCodes.Status403Forbidden,
                "PROFILE_SWITCH_CREDENTIAL_MISMATCH",
                "The authenticated credential does not belong to the current user.");
        }

        var claimedDeviceId = User.GetDeviceId();
        if (!string.IsNullOrWhiteSpace(claimedDeviceId)
            && !string.Equals(claimedDeviceId, credential.DeviceId, StringComparison.Ordinal))
        {
            throw new ProfileSelectorException(
                StatusCodes.Status403Forbidden,
                "PROFILE_SWITCH_DEVICE_MISMATCH",
                "The request device does not match the authenticated credential.");
        }

        return new ProfileSwitchRequestContext
        {
            CurrentUserId = credential.UserId,
            CallerCredentialRecordId = credential.Id,
            DeviceId = credential.DeviceId,
            DeviceName = credential.DeviceName,
            Client = credential.AppName,
            Version = credential.AppVersion,
            RemoteEndPoint = HttpContext.GetNormalizedRemoteIP().ToString()
        };
    }
}
