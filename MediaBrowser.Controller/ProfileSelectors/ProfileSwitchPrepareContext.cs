using System;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Carries the payload required to prepare a durable profile switch.
    /// </summary>
    public sealed class ProfileSwitchPrepareContext
    {
        /// <summary>
        /// Gets or sets the client-generated switch identifier.
        /// </summary>
        public Guid SwitchId { get; set; }

        /// <summary>
        /// Gets or sets the target profile user identifier.
        /// </summary>
        public Guid TargetProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the optional profile PIN.
        /// </summary>
        public string? Pin { get; set; }

        /// <summary>
        /// Gets or sets the authenticated request context.
        /// </summary>
        public ProfileSwitchRequestContext? RequestContext { get; set; }
    }
}
