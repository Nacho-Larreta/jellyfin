using System;

namespace MediaBrowser.Model.Dto
{
    /// <summary>
    /// Represents a profile card inside the selector.
    /// </summary>
    public class ProfileSelectorMemberDto
    {
        /// <summary>
        /// Gets or sets the profile user identifier.
        /// </summary>
        public Guid ProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the display name.
        /// </summary>
        public string? Name { get; set; }

        /// <summary>
        /// Gets or sets the primary image tag for the profile avatar.
        /// </summary>
        public string? PrimaryImageTag { get; set; }

        /// <summary>
        /// Gets or sets the display order.
        /// </summary>
        public int DisplayOrder { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the profile is visible in the selector.
        /// </summary>
        public bool IsVisible { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the profile requires a selector PIN.
        /// </summary>
        public bool RequiresPin { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the underlying user is disabled.
        /// </summary>
        public bool IsDisabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the underlying user is an administrator.
        /// </summary>
        public bool IsAdministrator { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the profile is the selector owner.
        /// </summary>
        public bool IsOwner { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the profile has parental-style restrictions.
        /// </summary>
        public bool HasParentalRestrictions { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether this is the current active profile for the device.
        /// </summary>
        public bool IsActive { get; set; }

        /// <summary>
        /// Gets or sets the last login date.
        /// </summary>
        public DateTime? LastLoginDate { get; set; }

        /// <summary>
        /// Gets or sets the last activity date.
        /// </summary>
        public DateTime? LastActivityDate { get; set; }
    }
}
