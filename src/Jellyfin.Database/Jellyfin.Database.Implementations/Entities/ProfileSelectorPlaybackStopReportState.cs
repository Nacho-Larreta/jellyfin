namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Defines persisted idempotent playback stop receipt states.
    /// </summary>
    public enum ProfileSelectorPlaybackStopReportState
    {
        /// <summary>
        /// The report key was reserved but the side effect has no classified outcome.
        /// </summary>
        Processing,

        /// <summary>
        /// No matching playback remained active.
        /// </summary>
        NotActive,

        /// <summary>
        /// The stop report was acknowledged.
        /// </summary>
        Acknowledged,

        /// <summary>
        /// The stop report has an unclassified failure and must not be replayed.
        /// </summary>
        Failed
    }
}
