using System;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Carries the authenticated, device-bound context for a profile switch request.
    /// </summary>
    public sealed class ProfileSwitchRequestContext
    {
        /// <summary>
        /// Gets or sets the authenticated caller user identifier.
        /// </summary>
        public Guid CurrentUserId { get; set; }

        /// <summary>
        /// Gets or sets the persisted credential row that authenticated the caller.
        /// </summary>
        public int CallerCredentialRecordId { get; set; }

        /// <summary>
        /// Gets or sets the bound device identifier.
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
        /// Gets or sets the normalized remote endpoint.
        /// </summary>
        public string? RemoteEndPoint { get; set; }
    }
}
