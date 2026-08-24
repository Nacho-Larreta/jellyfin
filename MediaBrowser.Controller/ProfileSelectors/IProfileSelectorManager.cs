using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Manages profile selectors, membership, device restore state and PIN-protected activation.
    /// </summary>
    public interface IProfileSelectorManager
    {
        /// <summary>
        /// Gets the selector visible to the current authenticated user.
        /// </summary>
        /// <param name="currentUserId">The current authenticated user id.</param>
        /// <param name="deviceId">The current device id.</param>
        /// <param name="includeHiddenProfiles">Whether to include hidden selector members.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The selector if available; otherwise <c>null</c>.</returns>
        Task<ProfileSelectorDto?> GetCurrentSelectorAsync(Guid currentUserId, string deviceId, bool includeHiddenProfiles, CancellationToken cancellationToken);

        /// <summary>
        /// Gets the selector owned by a given user.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        /// <param name="deviceId">The current device id.</param>
        /// <param name="includeHiddenProfiles">Whether to include hidden selector members.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The selector if configured; otherwise <c>null</c>.</returns>
        Task<ProfileSelectorDto?> GetSelectorForOwnerAsync(Guid ownerUserId, string deviceId, bool includeHiddenProfiles, CancellationToken cancellationToken);

        /// <summary>
        /// Determines whether a user is a member of the selector owned by another user.
        /// </summary>
        /// <param name="ownerUserId">The selector owner user id.</param>
        /// <param name="profileUserId">The candidate member user id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns><see langword="true"/> when the owner/profile relationship exists.</returns>
        Task<bool> IsProfileLinkedToOwnerAsync(Guid ownerUserId, Guid profileUserId, CancellationToken cancellationToken);

        /// <summary>
        /// Gets user ids that are used as secondary profile backing users.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The secondary profile user ids.</returns>
        Task<IReadOnlyList<Guid>> GetSecondaryProfileUserIdsAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Creates or updates a selector for the specified owner.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        /// <param name="deviceId">The current device id.</param>
        /// <param name="configuration">The desired selector configuration.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The updated selector.</returns>
        Task<ProfileSelectorDto> UpdateSelectorAsync(Guid ownerUserId, string deviceId, ProfileSelectorConfiguration configuration, CancellationToken cancellationToken);

        /// <summary>
        /// Activates a profile and issues a real user token for runtime usage.
        /// </summary>
        /// <param name="context">The activation context.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The activation result.</returns>
        Task<ProfileActivationResult> ActivateProfileAsync(ProfileActivationContext context, CancellationToken cancellationToken);

        /// <summary>
        /// Sets or replaces a profile PIN.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        /// <param name="profileUserId">The profile user id.</param>
        /// <param name="pin">The new PIN.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task SetProfilePinAsync(Guid ownerUserId, Guid profileUserId, string pin, CancellationToken cancellationToken);

        /// <summary>
        /// Clears a profile PIN.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        /// <param name="profileUserId">The profile user id.</param>
        /// <param name="currentPin">The current PIN to validate before clearing, if provided.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ClearProfilePinAsync(Guid ownerUserId, Guid profileUserId, string? currentPin, CancellationToken cancellationToken);
    }
}
