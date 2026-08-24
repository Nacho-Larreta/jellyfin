#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.ProfileSelectors;
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
    private readonly IProfileSelectorManager _profileSelectorManager;

    public SearchHistoryController(
        IProfileSearchHistoryManager profileSearchHistoryManager,
        IProfileSelectorManager profileSelectorManager)
    {
        _profileSearchHistoryManager = profileSearchHistoryManager;
        _profileSelectorManager = profileSelectorManager;
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
        if (!await CanAccessProfileAsync(ownerUserId, profileUserId, cancellationToken).ConfigureAwait(false))
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
        if (!await CanAccessProfileAsync(ownerUserId, profileUserId, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        if (request is null || string.IsNullOrWhiteSpace(request.SearchTerm))
        {
            return BadRequest("SearchTerm is required.");
        }

        var result = await _profileSearchHistoryManager.RecordSearchAsync(ownerUserId, profileUserId, request.SearchTerm, cancellationToken)
            .ConfigureAwait(false);
        return result switch
        {
            SearchHistoryRecordResult.Recorded => NoContent(),
            SearchHistoryRecordResult.EmptyTerm => BadRequest("SearchTerm must contain searchable characters."),
            SearchHistoryRecordResult.RawTermTooLong => BadRequest($"SearchTerm must not exceed {SearchHistoryUpdateRequestDto.MaxSearchTermLength} characters."),
            SearchHistoryRecordResult.TermTooLong => BadRequest("SearchTerm must not exceed 255 characters after whitespace normalization."),
            _ => throw new InvalidOperationException($"Unsupported search history record result: {result}")
        };
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult> ClearSearchHistory(
        [FromRoute] Guid ownerUserId,
        [FromRoute] Guid profileUserId,
        CancellationToken cancellationToken = default)
    {
        if (!await CanAccessProfileAsync(ownerUserId, profileUserId, cancellationToken).ConfigureAwait(false))
        {
            return Forbid();
        }

        await _profileSearchHistoryManager.ClearHistoryAsync(ownerUserId, profileUserId, cancellationToken)
            .ConfigureAwait(false);
        return NoContent();
    }

    private async Task<bool> CanAccessProfileAsync(Guid ownerUserId, Guid profileUserId, CancellationToken cancellationToken)
    {
        var currentUserId = User.GetUserId();
        return currentUserId.Equals(profileUserId)
               && await _profileSelectorManager.IsProfileLinkedToOwnerAsync(ownerUserId, profileUserId, cancellationToken).ConfigureAwait(false);
    }
}
