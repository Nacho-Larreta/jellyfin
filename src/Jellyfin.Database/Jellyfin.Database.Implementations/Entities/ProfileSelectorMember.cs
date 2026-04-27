using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Represents one real Jellyfin user exposed inside a selector.
    /// </summary>
    public class ProfileSelectorMember
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorMember"/> class.
        /// </summary>
        /// <param name="profileSelectorId">The selector id.</param>
        /// <param name="profileUserId">The profile user id.</param>
        public ProfileSelectorMember(Guid profileSelectorId, Guid profileUserId)
        {
            ProfileSelectorId = profileSelectorId;
            ProfileUserId = profileUserId;

            IsVisible = true;
            DisplayOrder = 0;
        }

        /// <summary>
        /// Gets the identity id.
        /// </summary>
        [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
        public int Id { get; private set; }

        /// <summary>
        /// Gets or sets the selector id.
        /// </summary>
        public Guid ProfileSelectorId { get; set; }

        /// <summary>
        /// Gets or sets the user id behind the profile.
        /// </summary>
        public Guid ProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the display order.
        /// </summary>
        public int DisplayOrder { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the profile is visible in the selector.
        /// </summary>
        public bool IsVisible { get; set; }

        /// <summary>
        /// Gets or sets the hashed PIN, if configured.
        /// </summary>
        [MaxLength(65535)]
        [StringLength(65535)]
        public string? PinHash { get; set; }

        /// <summary>
        /// Gets or sets the failed PIN attempt count.
        /// </summary>
        public int FailedPinAttemptCount { get; set; }

        /// <summary>
        /// Gets or sets the timestamp until which the PIN is locked.
        /// </summary>
        public DateTime? PinLockoutUntilUtc { get; set; }

        /// <summary>
        /// Gets or sets the last PIN failure date.
        /// </summary>
        public DateTime? LastFailedPinAttemptUtc { get; set; }
    }
}
