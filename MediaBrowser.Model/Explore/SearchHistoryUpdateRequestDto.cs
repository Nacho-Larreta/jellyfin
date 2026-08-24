#pragma warning disable CS1591

using System.ComponentModel.DataAnnotations;

namespace MediaBrowser.Model.Explore
{
    public class SearchHistoryUpdateRequestDto
    {
        public const int MaxSearchTermLength = 4096;

        [Required]
        [StringLength(MaxSearchTermLength, MinimumLength = 1)]
        public string? SearchTerm { get; set; }
    }
}
