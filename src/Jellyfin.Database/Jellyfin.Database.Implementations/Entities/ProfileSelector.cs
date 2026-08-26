using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Database.Implementations.Interfaces;

namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Represents a household-level profile selector owned by one Jellyfin user.
    /// </summary>
    public class ProfileSelector : IHasConcurrencyToken
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelector"/> class.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        public ProfileSelector(Guid ownerUserId)
        {
            OwnerUserId = ownerUserId;
            Members = new HashSet<ProfileSelectorMember>();
            DeviceStates = new HashSet<ProfileSelectorDeviceState>();

            Id = Guid.NewGuid();
            IsEnabled = true;
            AutoSelectSingleProfile = false;
            DateCreated = DateTime.UtcNow;
            DateModified = DateTime.UtcNow;
        }

        /// <summary>
        /// Gets or sets the selector id.
        /// </summary>
        public Guid Id { get; set; }

        /// <summary>
        /// Gets or sets the owner user id.
        /// </summary>
        public Guid OwnerUserId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the selector is enabled.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the client may auto-select a single profile.
        /// </summary>
        public bool AutoSelectSingleProfile { get; set; }

        /// <summary>
        /// Gets or sets the creation date.
        /// </summary>
        public DateTime DateCreated { get; set; }

        /// <summary>
        /// Gets or sets the last modification date.
        /// </summary>
        public DateTime DateModified { get; set; }

        /// <inheritdoc />
        [ConcurrencyCheck]
        public uint RowVersion { get; private set; }

        /// <summary>
        /// Gets the selector members.
        /// </summary>
        public virtual ICollection<ProfileSelectorMember> Members { get; private set; }

        /// <summary>
        /// Gets the per-device restore states.
        /// </summary>
        public virtual ICollection<ProfileSelectorDeviceState> DeviceStates { get; private set; }

        /// <inheritdoc />
        public void OnSavingChanges()
        {
            RowVersion++;
        }
    }
}
