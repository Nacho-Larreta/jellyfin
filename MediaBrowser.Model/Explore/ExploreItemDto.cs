#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Model.Explore
{
    public class ExploreItemDto
    {
        public ExploreItemDto()
        {
            Name = string.Empty;
            Children = Array.Empty<ExploreItemDto>();
        }

        public Guid Id { get; set; }

        public string Name { get; set; }

        public string? Overview { get; set; }

        public BaseItemKind Type { get; set; }

        public BaseItemDto? Item { get; set; }

        public int ItemCount { get; set; }

        public BaseItemDto? RepresentativeItem { get; set; }

        public IReadOnlyList<ExploreItemDto> Children { get; set; }
    }
}
