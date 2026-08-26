namespace Jellyfin.Database.Implementations.Entities
{
    /// <summary>
    /// Defines persisted profile switch operation states.
    /// </summary>
    public enum ProfileSelectorSwitchOperationState
    {
        /// <summary>
        /// The operation may be committed before expiry.
        /// </summary>
        Prepared,

        /// <summary>
        /// The remembered target and authentication reference were committed.
        /// </summary>
        Committed,

        /// <summary>
        /// The operation expired before commit.
        /// </summary>
        Expired,

        /// <summary>
        /// The operation was aborted before commit.
        /// </summary>
        Aborted
    }
}
