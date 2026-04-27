using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Stores the last selected profile for one selector/device pair.
    /// </summary>
    public class ProfileSelectorDeviceState
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorDeviceState"/> class.
        /// </summary>
        /// <param name="profileSelectorId">The selector id.</param>
        /// <param name="deviceId">The device id.</param>
        public ProfileSelectorDeviceState(Guid profileSelectorId, string deviceId)
        {
            ArgumentException.ThrowIfNullOrEmpty(deviceId);

            ProfileSelectorId = profileSelectorId;
            DeviceId = deviceId;
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
        /// Gets or sets the device id.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the currently remembered active profile user id.
        /// </summary>
        public Guid? ActiveProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the last activation date.
        /// </summary>
        public DateTime? LastActivatedUtc { get; set; }
    }
}
