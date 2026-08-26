using System;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Database.Implementations.Interfaces;

namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Stores a minimal idempotency receipt for an old-session playback stop report.
    /// </summary>
    public class ProfileSelectorPlaybackStopReport : IHasConcurrencyToken
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorPlaybackStopReport"/> class.
        /// </summary>
        /// <param name="reportKey">The deterministic report key.</param>
        /// <param name="switchId">The owning switch identifier.</param>
        /// <param name="callerUserId">The old-session user identifier.</param>
        /// <param name="deviceId">The old-session device identifier.</param>
        /// <param name="playSessionId">The captured play-session identifier.</param>
        /// <param name="requestHash">The non-secret request fingerprint.</param>
        /// <param name="dateCreatedUtc">The creation timestamp.</param>
        public ProfileSelectorPlaybackStopReport(
            string reportKey,
            Guid switchId,
            Guid callerUserId,
            string deviceId,
            string playSessionId,
            string requestHash,
            DateTime dateCreatedUtc)
        {
            ArgumentException.ThrowIfNullOrEmpty(reportKey);
            ArgumentException.ThrowIfNullOrEmpty(deviceId);
            ArgumentException.ThrowIfNullOrEmpty(playSessionId);
            ArgumentException.ThrowIfNullOrEmpty(requestHash);

            ReportKey = reportKey;
            SwitchId = switchId;
            CallerUserId = callerUserId;
            DeviceId = deviceId;
            PlaySessionId = playSessionId;
            RequestHash = requestHash;
            State = ProfileSelectorPlaybackStopReportState.Processing;
            DateCreatedUtc = dateCreatedUtc;
            DateModifiedUtc = dateCreatedUtc;
        }

        /// <summary>
        /// Gets or sets the deterministic report key.
        /// </summary>
        [MaxLength(64)]
        [StringLength(64)]
        public string ReportKey { get; set; }

        /// <summary>
        /// Gets or sets the owning switch identifier.
        /// </summary>
        public Guid SwitchId { get; set; }

        /// <summary>
        /// Gets or sets the old-session user identifier.
        /// </summary>
        public Guid CallerUserId { get; set; }

        /// <summary>
        /// Gets or sets the old-session device identifier.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string DeviceId { get; set; }

        /// <summary>
        /// Gets or sets the captured play-session identifier.
        /// </summary>
        [MaxLength(255)]
        [StringLength(255)]
        public string PlaySessionId { get; set; }

        /// <summary>
        /// Gets or sets the non-secret request fingerprint used for conflict detection.
        /// </summary>
        [MaxLength(64)]
        [StringLength(64)]
        public string RequestHash { get; set; }

        /// <summary>
        /// Gets or sets the classified receipt state.
        /// </summary>
        public ProfileSelectorPlaybackStopReportState State { get; set; }

        /// <summary>
        /// Gets or sets the creation timestamp.
        /// </summary>
        public DateTime DateCreatedUtc { get; set; }

        /// <summary>
        /// Gets or sets the last modification timestamp.
        /// </summary>
        public DateTime DateModifiedUtc { get; set; }

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
