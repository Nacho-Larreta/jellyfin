#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Explore
{
    public class CollectionOrganizationApplyRequestDto
    {
        public CollectionOrganizationApplyRequestDto()
        {
            Collections = Array.Empty<CollectionOrganizationCollectionDto>();
        }

        public Guid? UserId { get; set; }

        public bool LockCreatedCollections { get; set; } = true;

        public IReadOnlyList<CollectionOrganizationCollectionDto> Collections { get; set; }
    }
}
