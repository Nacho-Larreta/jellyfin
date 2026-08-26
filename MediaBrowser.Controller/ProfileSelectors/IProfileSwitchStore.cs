using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;

namespace MediaBrowser.Controller.ProfileSelectors
{
    /// <summary>
    /// Opens persistence units for the durable profile-switch use case.
    /// </summary>
    public interface IProfileSwitchStore
    {
        /// <summary>
        /// Opens one isolated persistence unit.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The opened persistence unit.</returns>
        Task<IProfileSwitchUnitOfWork> OpenAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Exposes only the persistence operations required by the profile-switch use case.
    /// </summary>
    public interface IProfileSwitchUnitOfWork : IAsyncDisposable
    {
        /// <summary>
        /// Finds a durable switch by its id.
        /// </summary>
        Task<ProfileSelectorSwitchOperation?> FindOperationAsync(Guid switchId, CancellationToken cancellationToken);

        /// <summary>
        /// Finds the operation currently holding a selector/device prepare slot.
        /// </summary>
        Task<ProfileSelectorSwitchOperation?> FindActiveOperationAsync(
            Guid selectorId,
            string deviceId,
            CancellationToken cancellationToken);

        /// <summary>
        /// Finds the selector associated with an owner or member caller.
        /// </summary>
        Task<ProfileSelector?> FindSelectorForCallerAsync(Guid callerUserId, CancellationToken cancellationToken);

        /// <summary>
        /// Finds a selector and its mutable policy state.
        /// </summary>
        Task<ProfileSelector?> FindSelectorAsync(Guid selectorId, CancellationToken cancellationToken);

        /// <summary>
        /// Finds a user together with the durable policy used by final commit validation.
        /// </summary>
        Task<User?> FindUserPolicyAsync(Guid userId, CancellationToken cancellationToken);

        /// <summary>
        /// Finds a playback receipt by deterministic report key.
        /// </summary>
        Task<ProfileSelectorPlaybackStopReport?> FindPlaybackReportAsync(
            string reportKey,
            CancellationToken cancellationToken);

        /// <summary>
        /// Determines whether the switch has any unsafe, unclassified playback receipt.
        /// </summary>
        Task<bool> HasUnclassifiedPlaybackReportAsync(Guid switchId, CancellationToken cancellationToken);

        /// <summary>
        /// Counts retained switch operations owned by one caller and canonical device.
        /// </summary>
        Task<int> CountRetainedOperationsAsync(
            Guid callerUserId,
            string deviceId,
            DateTime now,
            CancellationToken cancellationToken);

        /// <summary>
        /// Counts playback receipts retained by one switch.
        /// </summary>
        Task<int> CountPlaybackReportsAsync(Guid switchId, CancellationToken cancellationToken);

        /// <summary>
        /// Expires and purges a bounded global batch of operations.
        /// </summary>
        Task RunMaintenanceAsync(DateTime now, int batchSize, CancellationToken cancellationToken);

        /// <summary>
        /// Starts tracking a newly prepared operation.
        /// </summary>
        void AddOperation(ProfileSelectorSwitchOperation operation);

        /// <summary>
        /// Starts tracking a newly reserved playback receipt.
        /// </summary>
        void AddPlaybackReport(ProfileSelectorPlaybackStopReport report);

        /// <summary>
        /// Starts tracking the target authentication credential inside the commit transaction.
        /// </summary>
        void AddAuthenticationDevice(Device device);

        /// <summary>
        /// Adds a compare-and-swap assertion for the durable target-user policy snapshot.
        /// </summary>
        void AssertUserPolicyUnchanged(User user);

        /// <summary>
        /// Removes an operation and its dependent receipts.
        /// </summary>
        void RemoveOperation(ProfileSelectorSwitchOperation operation);

        /// <summary>
        /// Persists the tracked changes.
        /// </summary>
        Task SaveChangesAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Begins the durable server commit-point transaction.
        /// </summary>
        Task<IProfileSwitchTransaction> BeginTransactionAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Represents the profile-switch commit-point transaction.
    /// </summary>
    public interface IProfileSwitchTransaction : IAsyncDisposable
    {
        /// <summary>
        /// Commits the transaction.
        /// </summary>
        Task CommitAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Signals a provider-classified uniqueness race that the use case can resolve by reloading durable truth.
    /// </summary>
    public sealed class ProfileSwitchPersistenceConflictException : Exception
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSwitchPersistenceConflictException"/> class.
        /// </summary>
        /// <param name="innerException">The provider failure.</param>
        public ProfileSwitchPersistenceConflictException(Exception innerException)
            : base("A concurrent profile-switch persistence constraint was violated.", innerException)
        {
        }
    }
}
