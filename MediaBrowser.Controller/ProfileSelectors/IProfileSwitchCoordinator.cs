using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Session;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Coordinates durable, recoverable profile switches.
    /// </summary>
    public interface IProfileSwitchCoordinator
    {
        /// <summary>
        /// Validates and durably prepares a profile switch without changing active identity.
        /// </summary>
        /// <param name="context">The prepared switch request.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The durable switch state.</returns>
        Task<ProfileSwitchResult> PrepareAsync(ProfileSwitchPrepareContext context, CancellationToken cancellationToken);

        /// <summary>
        /// Revalidates and commits a prepared switch.
        /// </summary>
        /// <param name="switchId">The durable switch identifier.</param>
        /// <param name="requestContext">The authenticated request context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The committed runtime authentication result.</returns>
        Task<ProfileSwitchResult> CommitAsync(Guid switchId, ProfileSwitchRequestContext requestContext, CancellationToken cancellationToken);

        /// <summary>
        /// Resolves a durable switch after a lost response or process restart.
        /// </summary>
        /// <param name="switchId">The durable switch identifier.</param>
        /// <param name="requestContext">The authenticated request context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The current durable switch state.</returns>
        Task<ProfileSwitchResult> GetStatusAsync(Guid switchId, ProfileSwitchRequestContext requestContext, CancellationToken cancellationToken);

        /// <summary>
        /// Aborts a switch that has not crossed the server commit point.
        /// </summary>
        /// <param name="switchId">The durable switch identifier.</param>
        /// <param name="requestContext">The authenticated request context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The aborted durable switch state.</returns>
        Task<ProfileSwitchResult> AbortAsync(Guid switchId, ProfileSwitchRequestContext requestContext, CancellationToken cancellationToken);

        /// <summary>
        /// Idempotently reports the captured old playback session before commit.
        /// </summary>
        /// <param name="switchId">The durable switch identifier.</param>
        /// <param name="requestContext">The authenticated old-session request context.</param>
        /// <param name="playbackStopInfo">The captured old playback stop report.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The classified idempotent outcome.</returns>
        Task<ProfileSwitchPlaybackStopResult> ReportPlaybackStoppedAsync(
            Guid switchId,
            ProfileSwitchRequestContext requestContext,
            PlaybackStopInfo playbackStopInfo,
            CancellationToken cancellationToken);
    }
}
