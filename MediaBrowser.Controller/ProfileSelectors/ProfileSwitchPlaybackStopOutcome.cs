namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Defines classified outcomes for idempotent old-session playback reporting.
    /// </summary>
    public enum ProfileSwitchPlaybackStopOutcome
    {
        /// <summary>
        /// No matching old playback session remained active.
        /// </summary>
        NotActive,

        /// <summary>
        /// The old-session stop report was acknowledged exactly once.
        /// </summary>
        Acknowledged,

        /// <summary>
        /// The outcome cannot be safely retried or classified.
        /// </summary>
        Failed
    }
}
