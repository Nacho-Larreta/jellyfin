using System;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Database.Implementations.Interfaces;

namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Stores one durable, device-bound profile switch operation.
    /// </summary>
    public class ProfileSelectorSwitchOperation : IHasConcurrencyToken
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorSwitchOperation"/> class.
        /// </summary>
        /// <param name="switchId">The client-generated switch identifier.</param>
        /// <param name="profileSelectorId">The selector identifier.</param>
        /// <param name="ownerUserId">The selector owner user identifier.</param>
        /// <param name="callerUserId">The bound caller user identifier.</param>
        /// <param name="callerCredentialRecordId">The bound caller credential row.</param>
        /// <param name="targetProfileUserId">The target profile user identifier.</param>
        /// <param name="deviceId">The bound device identifier.</param>
        /// <param name="deviceName">The device display name.</param>
        /// <param name="client">The client application name.</param>
        /// <param name="version">The client version.</param>
        /// <param name="remoteEndPoint">The normalized remote endpoint.</param>
        /// <param name="dateCreatedUtc">The creation timestamp.</param>
        /// <param name="preparedExpiresUtc">The prepare expiry timestamp.</param>
        /// <param name="retainUntilUtc">The earliest retention boundary.</param>
        public ProfileSelectorSwitchOperation(
            Guid switchId,
            Guid profileSelectorId,
            Guid ownerUserId,
            Guid callerUserId,
            int callerCredentialRecordId,
            Guid targetProfileUserId,
            string deviceId,
            string deviceName,
            string client,
            string version,
            string remoteEndPoint,
            DateTime dateCreatedUtc,
            DateTime preparedExpiresUtc,
            DateTime retainUntilUtc)
        {
            ArgumentException.ThrowIfNullOrEmpty(deviceId);
            ArgumentException.ThrowIfNullOrEmpty(deviceName);
            ArgumentException.ThrowIfNullOrEmpty(client);
            ArgumentException.ThrowIfNullOrEmpty(version);
            ArgumentException.ThrowIfNullOrEmpty(remoteEndPoint);

            SwitchId = switchId;
            ProfileSelectorId = profileSelectorId;
            OwnerUserId = ownerUserId;
            CallerUserId = callerUserId;
            CallerCredentialRecordId = callerCredentialRecordId;
            TargetProfileUserId = targetProfileUserId;
            DeviceId = deviceId;
            ActiveDeviceId = deviceId;
            DeviceName = deviceName;
            Client = client;
            Version = version;
            RemoteEndPoint = remoteEndPoint;
            State = ProfileSelectorSwitchOperationState.Prepared;
            DateCreatedUtc = dateCreatedUtc;
            DateModifiedUtc = dateCreatedUtc;
            PreparedExpiresUtc = preparedExpiresUtc;
            RetainUntilUtc = retainUntilUtc;
        }

        /// <summary>
        /// Gets or sets the client-generated switch identifier.
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
        /// Gets or sets the bound caller user identifier.
        /// </summary>
        public Guid CallerUserId { get; set; }

        /// <summary>
        /// Gets or sets the persisted credential row that authenticated the preparing caller.
        /// </summary>
        public int CallerCredentialRecordId { get; set; }

        /// <summary>
        /// Gets or sets the target profile user identifier.
        /// </summary>
        public Guid TargetProfileUserId { get; set; }

        /// <summary>
        /// Gets or sets the bound device identifier.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the device identifier while this operation owns the active prepare slot.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string? ActiveDeviceId { get; set; }

        /// <summary>
        /// Gets or sets the device display name captured at prepare.
        /// </summary>
        [MaxLength(64)]
        [StringLength(64)]
        public string DeviceName { get; set; }

        /// <summary>
        /// Gets or sets the client application captured at prepare.
        /// </summary>
        [MaxLength(64)]
        [StringLength(64)]
        public string Client { get; set; }

        /// <summary>
        /// Gets or sets the client version captured at prepare.
        /// </summary>
        [MaxLength(32)]
        [StringLength(32)]
        public string Version { get; set; }

        /// <summary>
        /// Gets or sets the normalized remote endpoint captured at prepare.
        /// </summary>
        [MaxLength(64)]
        [StringLength(64)]
        public string RemoteEndPoint { get; set; }

        /// <summary>
        /// Gets or sets the one-way proof used only to classify same-payload prepare replay.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string? PinProofHash { get; set; }

        /// <summary>
        /// Gets or sets the durable operation state.
        /// </summary>
        public ProfileSelectorSwitchOperationState State { get; set; }

        /// <summary>
        /// Gets or sets the authentication device row created for this logical switch.
        /// </summary>
        public int? AuthenticationDeviceRecordId { get; set; }

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime DateCreatedUtc { get; set; }

        /// <summary>
        /// Gets or sets the last modification timestamp.
        /// </summary>
        public DateTime DateModifiedUtc { get; set; }

        /// <summary>
        /// Gets or sets the prepare expiry timestamp.
        /// </summary>
        public DateTime PreparedExpiresUtc { get; set; }

        /// <summary>
        /// Gets or sets the retention boundary for recovery/status replay.
        /// </summary>
        public DateTime RetainUntilUtc { get; set; }

        /// <inheritdoc />
        [ConcurrencyCheck]
        public uint RowVersion { get; private set; }

        /// <inheritdoc />
        public void OnSavingChanges()
        {
            RowVersion++;
        }
    }
}
