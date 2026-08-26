using System;

namespace Jellyfin.Server.Implementations.Users
{
    /// <summary>
    /// Configures durable profile switch expiry and recovery retention.
    /// </summary>
    public sealed class ProfileSwitchOptions
    {
        /// <summary>
        /// The configuration section name.
        /// </summary>
        public const string SectionName = "ProfileSwitch";

        /// <summary>
        /// The provisional default prepared-operation lifetime.
        /// </summary>
        public static readonly TimeSpan DefaultPreparedLifetime = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The provisional default terminal replay retention.
        /// </summary>
        public static readonly TimeSpan DefaultTerminalRetention = TimeSpan.FromDays(30);

        /// <summary>
        /// The default retained-operation quota for one caller user and device.
        /// </summary>
        public const int DefaultMaxRetainedOperationsPerCallerDevice = 512;

        /// <summary>
        /// The default receipt quota for one logical switch.
        /// </summary>
        public const int DefaultMaxPlaybackReportsPerSwitch = 8;

        /// <summary>
        /// The default maximum number of rows changed by one maintenance pass.
        /// </summary>
        public const int DefaultMaintenanceBatchSize = 128;

        /// <summary>
        /// Gets or sets how long a prepared operation remains committable.
        /// </summary>
        public TimeSpan PreparedLifetime { get; set; } = DefaultPreparedLifetime;

        /// <summary>
        /// Gets or sets how long terminal operations remain available for recovery replay.
        /// </summary>
        public TimeSpan TerminalRetention { get; set; } = DefaultTerminalRetention;

        /// <summary>
        /// Gets or sets the retained-operation quota for one caller user and device.
        /// </summary>
        public int MaxRetainedOperationsPerCallerDevice { get; set; } = DefaultMaxRetainedOperationsPerCallerDevice;

        /// <summary>
        /// Gets or sets the maximum playback receipts retained by one switch.
        /// </summary>
        public int MaxPlaybackReportsPerSwitch { get; set; } = DefaultMaxPlaybackReportsPerSwitch;

        /// <summary>
        /// Gets or sets the maximum rows changed by one global maintenance pass.
        /// </summary>
        public int MaintenanceBatchSize { get; set; } = DefaultMaintenanceBatchSize;

        /// <summary>
        /// Validates the configured durations.
        /// </summary>
        public void Validate()
        {
            if (PreparedLifetime <= TimeSpan.Zero)
            {
                throw new InvalidOperationException("ProfileSwitch:PreparedLifetime must be greater than zero.");
            }

            if (TerminalRetention <= PreparedLifetime)
            {
                throw new InvalidOperationException("ProfileSwitch:TerminalRetention must exceed PreparedLifetime.");
            }

            if (MaxRetainedOperationsPerCallerDevice <= 0)
            {
                throw new InvalidOperationException("ProfileSwitch:MaxRetainedOperationsPerCallerDevice must be greater than zero.");
            }

            if (MaxPlaybackReportsPerSwitch <= 0)
            {
                throw new InvalidOperationException("ProfileSwitch:MaxPlaybackReportsPerSwitch must be greater than zero.");
            }

            if (MaintenanceBatchSize <= 0)
            {
                throw new InvalidOperationException("ProfileSwitch:MaintenanceBatchSize must be greater than zero.");
            }

        }
    }
}
