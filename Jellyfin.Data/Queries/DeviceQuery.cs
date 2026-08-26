using System;

namespace Jellyfin.Data.Queries
{
    /// <summary>
    /// A query to retrieve devices.
    /// </summary>
    public class DeviceQuery : PaginatedQuery
    {
        /// <summary>
        /// Gets or sets the user id of the device.
        /// </summary>
        public Guid? UserId { get; set; }

        /// <summary>
        /// Gets or sets the device id.
        /// </summary>
        public string? DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the access token.
        /// </summary>
        public string? AccessToken { get; set; }

        /// <summary>
        /// Gets or sets the durable profile switch identifier that created the device token.
        /// </summary>
        public Guid? ProfileSwitchId { get; set; }
    }
}
