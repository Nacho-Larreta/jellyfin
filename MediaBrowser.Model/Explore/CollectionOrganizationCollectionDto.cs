#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Explore
{
    public class CollectionOrganizationCollectionDto
    {
        public CollectionOrganizationCollectionDto()
        {
            Name = string.Empty;
            ItemIds = Array.Empty<Guid>();
            RemoveItemIds = Array.Empty<Guid>();
            Children = Array.Empty<CollectionOrganizationCollectionDto>();
        }

        public Guid? Id { get; set; }

        public string Name { get; set; }

        public IReadOnlyList<Guid> ItemIds { get; set; }

        public IReadOnlyList<Guid> RemoveItemIds { get; set; }

        public IReadOnlyList<CollectionOrganizationCollectionDto> Children { get; set; }
    }
}
