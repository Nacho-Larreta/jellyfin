using System;
using MediaBrowser.Controller.Authentication;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Represents the result of activating a profile.
    /// </summary>
    public class ProfileActivationResult
    {
        /// <summary>
        /// Gets or sets the selector identifier.
        /// </summary>
        public Guid ProfileSelectorId { get; set; }

        /// <summary>
        /// Gets or sets the owner user id.
        /// </summary>
        public Guid OwnerUserId { get; set; }

        /// <summary>
        /// Gets or sets the active profile user id.
        /// </summary>
        public Guid ActiveProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the real runtime authentication result for the activated profile.
        /// </summary>
        public AuthenticationResult? AuthenticationResult { get; set; }
    }
}
