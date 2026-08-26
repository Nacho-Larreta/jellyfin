namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Defines durable server-side profile switch states.
    /// </summary>
    public enum ProfileSwitchState
    {
        /// <summary>
        /// The target was validated and may be committed before expiry.
        /// </summary>
        Prepared,

        /// <summary>
        /// Remembered identity and authentication reference crossed the commit point.
        /// </summary>
        Committed,

        /// <summary>
        /// The prepared operation expired before commit.
        /// </summary>
        Expired,

        /// <summary>
        /// The operation was explicitly or deterministically aborted before commit.
        /// </summary>
        Aborted
    }
}
