using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Explore;

namespace MediaBrowser.Controller.Search
{
    /// <summary>
    /// Stores and retrieves search history scoped to a profile.
    /// </summary>
    public interface IProfileSearchHistoryManager
    {
        /// <summary>
        /// Gets recent search terms for a profile.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        /// <param name="profileUserId">The active profile user id.</param>
        /// <param name="limit">The maximum number of terms to return.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The recent profile search terms.</returns>
        Task<IReadOnlyList<SearchHistoryEntryDto>> GetHistoryAsync(Guid ownerUserId, Guid profileUserId, int limit, CancellationToken cancellationToken);

        /// <summary>
        /// Records a search term for a profile.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        /// <param name="profileUserId">The active profile user id.</param>
        /// <param name="searchTerm">The submitted search term.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The record outcome.</returns>
        Task<SearchHistoryRecordResult> RecordSearchAsync(Guid ownerUserId, Guid profileUserId, string searchTerm, CancellationToken cancellationToken);

        /// <summary>
        /// Clears search history for a profile.
        /// </summary>
        /// <param name="ownerUserId">The owner user id.</param>
        /// <param name="profileUserId">The active profile user id.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        Task ClearHistoryAsync(Guid ownerUserId, Guid profileUserId, CancellationToken cancellationToken);
    }
}
