#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Search;
using MediaBrowser.Model.Explore;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

[Route("Users/{ownerUserId}/Profiles/{profileUserId}/Search/History")]
[Authorize(Policy = Policies.IgnoreParentalControl)]
[Tags("Search")]
public class SearchHistoryController : BaseJellyfinApiController
{
    private readonly IProfileSearchHistoryManager _profileSearchHistoryManager;

    public SearchHistoryController(IProfileSearchHistoryManager profileSearchHistoryManager)
    {
        _profileSearchHistoryManager = profileSearchHistoryManager;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SearchHistoryEntryDto>>> GetSearchHistory(
        [FromRoute] Guid ownerUserId,
        [FromRoute] Guid profileUserId,
        [FromQuery] int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!CanAccessProfile(ownerUserId, profileUserId))
        {
            return Forbid();
        }

        var history = await _profileSearchHistoryManager.GetHistoryAsync(ownerUserId, profileUserId, limit, cancellationToken)
            .ConfigureAwait(false);
        return Ok(history);
    }

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> RecordSearchHistory(
        [FromRoute] Guid ownerUserId,
        [FromRoute] Guid profileUserId,
        [FromBody] SearchHistoryUpdateRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!CanAccessProfile(ownerUserId, profileUserId))
        {
            return Forbid();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            return BadRequest("SearchTerm is required.");
        }

        await _profileSearchHistoryManager.RecordSearchAsync(ownerUserId, profileUserId, request.SearchTerm, cancellationToken)
            .ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> ClearSearchHistory(
        [FromRoute] Guid ownerUserId,
        [FromRoute] Guid profileUserId,
        CancellationToken cancellationToken = default)
    {
        if (!CanAccessProfile(ownerUserId, profileUserId))
        {
            return Forbid();
        }

        await _profileSearchHistoryManager.ClearHistoryAsync(ownerUserId, profileUserId, cancellationToken)
            .ConfigureAwait(false);
        return NoContent();
    }

    private bool CanAccessProfile(Guid ownerUserId, Guid profileUserId)
    {
        var currentUserId = User.GetUserId();
        return User.IsInRole(UserRoles.Administrator)
            || currentUserId.Equals(ownerUserId)
            || currentUserId.Equals(profileUserId);
    }
}
