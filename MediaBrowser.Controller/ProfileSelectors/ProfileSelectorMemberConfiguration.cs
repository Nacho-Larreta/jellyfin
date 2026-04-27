using System;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Represents the desired persisted state of one selector member.
    /// </summary>
    public class ProfileSelectorMemberConfiguration
    {
        /// <summary>
        /// Gets or sets the profile user id.
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
    }
}
