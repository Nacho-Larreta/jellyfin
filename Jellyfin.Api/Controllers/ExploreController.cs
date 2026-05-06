#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Helpers;
using Jellyfin.Api.ModelBinders;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Explore;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Api.Controllers;

[Route("Users/{userId}/Explore")]
[Authorize(Policy = Policies.IgnoreParentalControl)]
[Tags("Explore")]
public class ExploreController : BaseJellyfinApiController
{
    private static readonly BaseItemKind[] DefaultExploreItemTypes =
    [
        BaseItemKind.Movie,
        BaseItemKind.Series,
        BaseItemKind.Episode,
        BaseItemKind.Video,
        BaseItemKind.Program
    ];

    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IDtoService _dtoService;

    public ExploreController(
        IUserManager userManager,
        ILibraryManager libraryManager,
        ICollectionManager collectionManager,
        IDtoService dtoService)
    {
        _userManager = userManager;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _dtoService = dtoService;
    }

    [HttpGet("Genres")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<ExploreSectionDto> GetGenres(
        [FromRoute] Guid userId,
        [FromQuery, Range(1, 50)] int limit = 12,
        [FromQuery, ModelBinder(typeof(CommaDelimitedCollectionModelBinder))] BaseItemKind[] includeItemTypes = default!)
    {
        var user = GetAllowedUser(userId);
        var itemTypes = includeItemTypes is null || includeItemTypes.Length == 0 ? DefaultExploreItemTypes : includeItemTypes;
        var dtoOptions = new DtoOptions();

        var result = _libraryManager.GetGenres(new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = itemTypes,
            Limit = limit,
            DtoOptions = dtoOptions,
            EnableTotalRecordCount = true
        });

        var items = result.Items.Select(tuple =>
        {
            var (genre, counts) = tuple;
            var representative = GetRepresentativeGenreItem(user, genre.Name, itemTypes, dtoOptions);

            return new ExploreItemDto
            {
                Id = genre.Id,
                Name = genre.Name,
                Overview = genre.Overview,
                Type = genre.GetBaseItemKind(),
                Item = _dtoService.GetItemByNameDto(genre, dtoOptions, null, user),
                ItemCount = GetTotalCount(counts),
                RepresentativeItem = representative is null ? null : _dtoService.GetBaseItemDto(representative, dtoOptions, user)
            };
        }).ToArray();

        return new ExploreSectionDto
        {
            Id = "genres",
            Name = "Genres",
            Items = items,
            TotalRecordCount = result.TotalRecordCount
        };
    }

    [HttpGet("Collections")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ExploreSectionDto>> GetCollections(
        [FromRoute] Guid userId,
        [FromQuery, Range(1, 100)] int limit = 50,
        [FromQuery, Range(1, 5)] int depth = 2)
    {
        var user = GetAllowedUser(userId);
        var dtoOptions = new DtoOptions();
        var collectionsFolder = await _collectionManager.GetCollectionsFolder(false).ConfigureAwait(false);
        if (collectionsFolder is null)
        {
            return new ExploreSectionDto
            {
                Id = "collections",
                Name = "Collections"
            };
        }

        var query = new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Limit = limit,
            DtoOptions = dtoOptions,
            EnableTotalRecordCount = true,
            OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
        };

        var boxSets = collectionsFolder.GetRecursiveChildren(user, query, out var totalCount)
            .OfType<BoxSet>()
            .Where(collection => collection.IsVisible(user))
            .Take(limit)
            .ToArray();

        var byParentId = boxSets
            .GroupBy(collection => collection.ParentId)
            .ToDictionary(group => group.Key, group => group.OrderBy(collection => collection.SortName, StringComparer.OrdinalIgnoreCase).ToArray());

        var rootParentIds = new HashSet<Guid> { collectionsFolder.Id };
        var linkedChildren = boxSets.SelectMany(collection => collection.LinkedChildren.Select(link => link.ItemId)).ToHashSet();
        var roots = boxSets
            .Where(collection => rootParentIds.Contains(collection.ParentId) || !linkedChildren.Contains(collection.Id))
            .OrderBy(collection => collection.SortName, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(collection => ToCollectionExploreItem(collection, user, dtoOptions, byParentId, depth))
            .ToArray();

        return new ExploreSectionDto
        {
            Id = "collections",
            Name = "Collections",
            Items = roots,
            TotalRecordCount = totalCount
        };
    }

    private User GetAllowedUser(Guid userId)
    {
        var allowedUserId = RequestHelpers.GetUserId(User, userId);
        return _userManager.GetUserById(allowedUserId)
            ?? throw new ResourceNotFoundException($"User {allowedUserId} not found.");
    }

    private BaseItem? GetRepresentativeGenreItem(User user, string genreName, IReadOnlyList<BaseItemKind> itemTypes, DtoOptions dtoOptions)
    {
        var items = _libraryManager.GetItemList(new InternalItemsQuery(user)
        {
            Recursive = true,
            Genres = [genreName],
            IncludeItemTypes = itemTypes.ToArray(),
            Limit = 1,
            DtoOptions = dtoOptions,
            OrderBy = [(ItemSortBy.DateCreated, SortOrder.Descending)]
        });

        return items.Count == 0 ? null : items[0];
    }

    private ExploreItemDto ToCollectionExploreItem(
        BoxSet collection,
        User user,
        DtoOptions dtoOptions,
        IReadOnlyDictionary<Guid, BoxSet[]> childrenByParentId,
        int depth)
    {
        var directChildren = collection.GetChildren(user, true, new InternalItemsQuery(user)
        {
            DtoOptions = dtoOptions,
            EnableTotalRecordCount = true
        });

        var representative = directChildren.FirstOrDefault(item => item is not BoxSet)
            ?? (directChildren.Count == 0 ? null : directChildren[0]);

        childrenByParentId.TryGetValue(collection.Id, out var nestedCollections);
        var nestedItems = depth <= 1 || nestedCollections is null
            ? Array.Empty<ExploreItemDto>()
            : nestedCollections
                .Select(child => ToCollectionExploreItem(child, user, dtoOptions, childrenByParentId, depth - 1))
                .ToArray();

        return new ExploreItemDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Overview = collection.Overview,
            Type = collection.GetBaseItemKind(),
            Item = _dtoService.GetBaseItemDto(collection, dtoOptions, user),
            ItemCount = directChildren.Count,
            RepresentativeItem = representative is null ? null : _dtoService.GetBaseItemDto(representative, dtoOptions, user),
            Children = nestedItems
        };
    }

    private static int GetTotalCount(ItemCounts counts)
    {
        var total = counts.TotalItemCount();
        return total == 0 ? counts.ItemCount : total;
    }
}
