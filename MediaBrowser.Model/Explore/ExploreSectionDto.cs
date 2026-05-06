#pragma warning disable CS1591

using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Explore
{
    public class ExploreSectionDto
    {
        public ExploreSectionDto()
        {
            Id = string.Empty;
            Name = string.Empty;
            Items = Array.Empty<ExploreItemDto>();
        }

        public string Id { get; set; }

        public string Name { get; set; }

        public IReadOnlyList<ExploreItemDto> Items { get; set; }

        public int TotalRecordCount { get; set; }
    }
}
