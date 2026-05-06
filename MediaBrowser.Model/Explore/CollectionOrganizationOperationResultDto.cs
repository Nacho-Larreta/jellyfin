#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Explore
{
    public class CollectionOrganizationOperationResultDto
    {
        public CollectionOrganizationOperationResultDto()
        {
            CollectionName = string.Empty;
            Operation = string.Empty;
            Warnings = Array.Empty<string>();
        }

        public Guid CollectionId { get; set; }

        public string CollectionName { get; set; }

        public string Operation { get; set; }

        public int AddedItemCount { get; set; }

        public int RemovedItemCount { get; set; }

        public IReadOnlyList<string> Warnings { get; set; }
    }
}
