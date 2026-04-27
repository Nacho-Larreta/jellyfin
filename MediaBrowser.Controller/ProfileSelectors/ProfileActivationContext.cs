using System;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Carries the request-scoped data needed to activate a profile.
    /// </summary>
    public class ProfileActivationContext
    {
        /// <summary>
        /// Gets or sets the authenticated user making the request.
        /// </summary>
        public Guid CurrentUserId { get; set; }

        /// <summary>
        /// Gets or sets the target profile user id.
        /// </summary>
        public Guid ProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the current device id.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the device display name.
        /// </summary>
        public string? DeviceName { get; set; }

        /// <summary>
        /// Gets or sets the client application name.
        /// </summary>
        public string? Client { get; set; }

        /// <summary>
        /// Gets or sets the client version.
        /// </summary>
        public string? Version { get; set; }

        /// <summary>
        /// Gets or sets the remote endpoint.
        /// </summary>
        public string? RemoteEndPoint { get; set; }

        /// <summary>
        /// Gets or sets the optional selector PIN.
        /// </summary>
        public string? Pin { get; set; }
    }
}
