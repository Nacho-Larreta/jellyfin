#pragma warning disable CS1591

using System;

namespace MediaBrowser.Model.Explore
{
    public class SearchHistoryEntryDto
    {
        public SearchHistoryEntryDto()
        {
            SearchTerm = string.Empty;
        }

        public string SearchTerm { get; set; }

        public int HitCount { get; set; }

        public DateTime LastSearchedUtc { get; set; }
    }
}
