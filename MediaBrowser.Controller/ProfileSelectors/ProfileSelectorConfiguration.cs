using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Represents the desired persisted shape of a selector.
    /// </summary>
    public class ProfileSelectorConfiguration
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorConfiguration"/> class.
        /// </summary>
        public ProfileSelectorConfiguration()
        {
            Profiles = new List<ProfileSelectorMemberConfiguration>();
        }

        /// <summary>
        /// Gets or sets a value indicating whether the selector is enabled.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the client may auto-select when only one profile is visible.
        /// </summary>
        public bool AutoSelectSingleProfile { get; set; }

        /// <summary>
        /// Gets or sets the profiles that belong to the selector.
        /// </summary>
        public List<ProfileSelectorMemberConfiguration> Profiles { get; set; }
    }
}
