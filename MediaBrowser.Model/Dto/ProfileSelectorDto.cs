using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Dto
{
    /// <summary>
    /// Represents the profile selector available to the current runtime context.
    /// </summary>
    public class ProfileSelectorDto
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorDto"/> class.
        /// </summary>
        public ProfileSelectorDto()
        {
            Profiles = new List<ProfileSelectorMemberDto>();
        }

        /// <summary>
        /// Gets or sets the selector identifier.
        /// </summary>
        public Guid ProfileSelectorId { get; set; }

        /// <summary>
        /// Gets or sets the owner user identifier.
        /// </summary>
        public Guid OwnerUserId { get; set; }

        /// <summary>
        /// Gets or sets the owner user name.
        /// </summary>
        public string? OwnerUserName { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the selector is enabled.
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the client may auto-select a single profile.
        /// </summary>
        public bool AutoSelectSingleProfile { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the current authenticated user can manage the selector.
        /// </summary>
        public bool CanManageProfiles { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the current authenticated user is the owner of the selector.
        /// </summary>
        public bool IsCurrentUserOwner { get; set; }

        /// <summary>
        /// Gets or sets the last active profile for the current device.
        /// </summary>
        public Guid? CurrentDeviceProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the last active profile requires a PIN before restore.
        /// </summary>
        public bool CurrentDeviceProfileRequiresPin { get; set; }

        /// <summary>
        /// Gets or sets the profiles exposed by the selector.
        /// </summary>
        public List<ProfileSelectorMemberDto> Profiles { get; set; }
    }
}
