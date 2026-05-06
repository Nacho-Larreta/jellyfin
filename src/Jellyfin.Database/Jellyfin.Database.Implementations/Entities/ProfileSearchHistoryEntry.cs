using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Stores one profile-scoped search history term.
    /// </summary>
    public class ProfileSearchHistoryEntry
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSearchHistoryEntry"/> class.
        /// </summary>
        public ProfileSearchHistoryEntry()
        {
            SearchTerm = string.Empty;
            SearchTermNormalized = string.Empty;
            DateCreatedUtc = DateTime.UtcNow;
            LastSearchedUtc = DateCreatedUtc;
        }

        /// <summary>
        /// Gets the identity id.
        /// </summary>
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; private set; }

        /// <summary>
        /// Gets or sets the main account that owns this profile selector.
        /// </summary>
        public Guid OwnerUserId { get; set; }

        /// <summary>
        /// Gets or sets the profile user that performed the search.
        /// </summary>
        public Guid ProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the original display search term.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string SearchTerm { get; set; }

        /// <summary>
        /// Gets or sets the normalized search term used for uniqueness.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string SearchTermNormalized { get; set; }

        /// <summary>
        /// Gets or sets how often this term was submitted.
        /// </summary>
        public int HitCount { get; set; }

        /// <summary>
        /// Gets or sets when the term was first searched.
        /// </summary>
        public DateTime DateCreatedUtc { get; set; }

        /// <summary>
        /// Gets or sets when the term was last searched.
        /// </summary>
        public DateTime LastSearchedUtc { get; set; }
    }
}
