#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Api.Constants;
using Jellyfin.Api.Extensions;
using Jellyfin.Api.Helpers;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Explore;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using SortOrder = Jellyfin.Database.Implementations.Enums.SortOrder;

namespace Jellyfin.Api.Controllers;

[Route("CollectionOrganization")]
[Authorize(Policy = Policies.CollectionManagement)]
[Tags("Collection Organization")]
public class CollectionOrganizationController : BaseJellyfinApiController
{
    private readonly IUserManager _userManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IDtoService _dtoService;

    public CollectionOrganizationController(
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

    [HttpGet("Export")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ExploreSectionDto>> ExportCollections(
        [FromQuery] Guid? userId,
        [FromQuery, Range(1, 5)] int depth = 3)
    {
        var targetUser = GetTargetUser(userId);
        var collectionsFolder = await _collectionManager.GetCollectionsFolder(false).ConfigureAwait(false);
        if (collectionsFolder is null)
        {
            return new ExploreSectionDto
            {
                Id = "collections",
                Name = "Collections"
            };
        }

        var dtoOptions = new DtoOptions();
        var collections = collectionsFolder.GetRecursiveChildren(
            targetUser,
            new InternalItemsQuery(targetUser)
            {
                Recursive = true,
                IncludeItemTypes = [BaseItemKind.BoxSet],
                DtoOptions = dtoOptions,
                OrderBy = [(ItemSortBy.SortName, SortOrder.Ascending)]
            },
            out var totalCount).OfType<BoxSet>().ToArray();

        var byParentId = collections
            .GroupBy(collection => collection.ParentId)
            .ToDictionary(group => group.Key, group => group.OrderBy(collection => collection.SortName, StringComparer.OrdinalIgnoreCase).ToArray());

        var roots = collections
            .Where(collection => collection.ParentId.Equals(collectionsFolder.Id))
            .Select(collection => ToExportItem(collection, targetUser, dtoOptions, byParentId, depth))
            .ToArray();

        return new ExploreSectionDto
        {
            Id = "collections",
            Name = "Collections",
            Items = roots,
            TotalRecordCount = totalCount
        };
    }

    [HttpPost("Apply")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CollectionOrganizationApplyResultDto>> ApplyOrganization(
        [FromBody] CollectionOrganizationApplyRequestDto request)
    {
        if (request is null)
        {
            return BadRequest("Request body is required.");
        }

        if (request.Collections.Count == 0)
        {
            return BadRequest("At least one collection is required.");
        }

        var targetUser = GetTargetUser(request.UserId);
        var operations = new List<CollectionOrganizationOperationResultDto>();

        foreach (var collection in request.Collections)
        {
            try
            {
                await ApplyCollectionNode(collection, targetUser.Id, null, request.LockCreatedCollections, operations)
                    .ConfigureAwait(false);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        return new CollectionOrganizationApplyResultDto
        {
            Operations = operations
        };
    }

    private User GetTargetUser(Guid? requestedUserId)
    {
        var userId = requestedUserId.HasValue
            ? RequestHelpers.GetUserId(User, requestedUserId.Value)
            : User.GetUserId();

        return _userManager.GetUserById(userId)
            ?? throw new ResourceNotFoundException($"User {userId} not found.");
    }

    private async Task<Guid> ApplyCollectionNode(
        CollectionOrganizationCollectionDto node,
        Guid userId,
        Guid? parentId,
        bool lockCreatedCollections,
        ICollection<CollectionOrganizationOperationResultDto> operations)
    {
        if (string.IsNullOrWhiteSpace(node.Name) && !node.Id.HasValue)
        {
            throw new ArgumentException("Collection name is required when Id is not provided.", nameof(node));
        }

        var collectionName = node.Name.Trim();
        var collection = node.Id.HasValue
            ? GetCollectionById(node.Id.Value)
            : FindCollectionByName(collectionName, parentId);

        if (collection is null && collectionName.Length == 0)
        {
            throw new ArgumentException("Collection name is required when the supplied Id does not match an existing collection.", nameof(node));
        }

        var warnings = new List<string>();
        var operation = "Updated";
        if (collection is null)
        {
            collection = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = collectionName,
                ParentId = parentId,
                IsLocked = lockCreatedCollections,
                ItemIdList = node.ItemIds.Select(id => id.ToString("N")).ToArray(),
                UserIds = [userId]
            }).ConfigureAwait(false);
            operation = "Created";
        }
        else if (node.ItemIds.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(collection.Id, node.ItemIds).ConfigureAwait(false);
        }

        if (node.RemoveItemIds.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(collection.Id, node.RemoveItemIds).ConfigureAwait(false);
        }

        operations.Add(new CollectionOrganizationOperationResultDto
        {
            CollectionId = collection.Id,
            CollectionName = collection.Name,
            Operation = operation,
            AddedItemCount = node.ItemIds.Count,
            RemovedItemCount = node.RemoveItemIds.Count,
            Warnings = warnings
        });

        foreach (var child in node.Children)
        {
            await ApplyCollectionNode(child, userId, collection.Id, lockCreatedCollections, operations)
                .ConfigureAwait(false);
        }

        return collection.Id;
    }

    private BoxSet? GetCollectionById(Guid collectionId)
    {
        return _libraryManager.GetItemById(collectionId) as BoxSet;
    }

    private BoxSet? FindCollectionByName(string name, Guid? parentId)
    {
        var query = new InternalItemsQuery
        {
            IncludeItemTypes = [BaseItemKind.BoxSet],
            Name = name.Trim()
        };

        if (parentId.HasValue)
        {
            query.ParentId = parentId.Value;
        }

        return _libraryManager.GetItemList(query).OfType<BoxSet>().FirstOrDefault();
    }

    private ExploreItemDto ToExportItem(
        BoxSet collection,
        User user,
        DtoOptions dtoOptions,
        IReadOnlyDictionary<Guid, BoxSet[]> childrenByParentId,
        int depth)
    {
        var children = collection.GetChildren(user, true, new InternalItemsQuery(user)
        {
            DtoOptions = dtoOptions
        });

        childrenByParentId.TryGetValue(collection.Id, out var nestedCollections);
        var nested = depth <= 1 || nestedCollections is null
            ? Array.Empty<ExploreItemDto>()
            : nestedCollections
                .Select(child => ToExportItem(child, user, dtoOptions, childrenByParentId, depth - 1))
                .ToArray();

        return new ExploreItemDto
        {
            Id = collection.Id,
            Name = collection.Name,
            Overview = collection.Overview,
            Type = collection.GetBaseItemKind(),
            Item = _dtoService.GetBaseItemDto(collection, dtoOptions, user),
            ItemCount = children.Count,
            RepresentativeItem = children.FirstOrDefault(item => item is not BoxSet) is { } representative
                ? _dtoService.GetBaseItemDto(representative, dtoOptions, user)
                : null,
            Children = nested
        };
    }
}
