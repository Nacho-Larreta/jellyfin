namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Represents the stable result of an idempotent old-session stop report.
    /// </summary>
    public sealed class ProfileSwitchPlaybackStopResult
    {
        /// <summary>
        /// Gets or sets the stable report key derived from switch and play-session identity.
        /// </summary>
        public string ReportKey { get; set; } = string.Empty;

        /// <summary>
        /// Gets or sets the classified result.
        /// </summary>
        public ProfileSwitchPlaybackStopOutcome Outcome { get; set; }
    }
}
