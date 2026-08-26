using System;
using MediaBrowser.Controller.Authentication;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Represents the public durable state of a profile switch.
    /// </summary>
    public sealed class ProfileSwitchResult
    {
        /// <summary>
        /// Gets or sets the switch identifier.
        /// </summary>
        public Guid SwitchId { get; set; }

        /// <summary>
        /// Gets or sets the selector identifier.
        /// </summary>
        public Guid ProfileSelectorId { get; set; }

        /// <summary>
        /// Gets or sets the selector owner user identifier.
        /// </summary>
        public Guid OwnerUserId { get; set; }

        /// <summary>
        /// Gets or sets the target profile user identifier.
        /// </summary>
        public Guid TargetProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the durable state.
        /// </summary>
        public ProfileSwitchState State { get; set; }

        /// <summary>
        /// Gets or sets the prepare expiry timestamp.
        /// </summary>
        public DateTime PreparedExpiresUtc { get; set; }

        /// <summary>
        /// Gets or sets the target authentication data for a committed switch.
        /// </summary>
        public AuthenticationResult? AuthenticationResult { get; set; }
    }
}
