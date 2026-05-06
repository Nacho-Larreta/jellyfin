#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Explore
{
    public class CollectionOrganizationApplyResultDto
    {
        public CollectionOrganizationApplyResultDto()
        {
            Operations = Array.Empty<CollectionOrganizationOperationResultDto>();
        }

        public IReadOnlyList<CollectionOrganizationOperationResultDto> Operations { get; set; }
    }
}
