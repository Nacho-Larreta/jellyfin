using System;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.ProfileSelectors;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Jellyfin.Server.Implementations.Users.Persistence
{
    /// <summary>
    /// Adapts EF Core persistence to the profile-switch application port.
    /// </summary>
    public sealed class EfProfileSwitchStore : IProfileSwitchStore
    {
        private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="EfProfileSwitchStore"/> class.
        /// </summary>
        /// <param name="dbContextFactory">The Jellyfin database-context factory.</param>
        public EfProfileSwitchStore(IDbContextFactory<JellyfinDbContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

        /// <inheritdoc />
        public async Task<IProfileSwitchUnitOfWork> OpenAsync(CancellationToken cancellationToken)
            => new EfProfileSwitchUnitOfWork(
                await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false));

        private sealed class EfProfileSwitchUnitOfWork : IProfileSwitchUnitOfWork
        {
            private readonly JellyfinDbContext _dbContext;

            public EfProfileSwitchUnitOfWork(JellyfinDbContext dbContext)
            {
                _dbContext = dbContext;
            }

            public Task<ProfileSelectorSwitchOperation?> FindOperationAsync(
                Guid switchId,
                CancellationToken cancellationToken)
                => _dbContext.ProfileSelectorSwitchOperations
                    .SingleOrDefaultAsync(operation => operation.SwitchId.Equals(switchId), cancellationToken);

            public Task<ProfileSelectorSwitchOperation?> FindActiveOperationAsync(
                Guid selectorId,
                string deviceId,
                CancellationToken cancellationToken)
                => _dbContext.ProfileSelectorSwitchOperations
                    .SingleOrDefaultAsync(
                        operation => operation.ProfileSelectorId.Equals(selectorId)
                                     && operation.ActiveDeviceId == deviceId,
                        cancellationToken);

            public Task<ProfileSelector?> FindSelectorForCallerAsync(
                Guid callerUserId,
                CancellationToken cancellationToken)
                => QuerySelectors()
                    .SingleOrDefaultAsync(
                        selector => selector.OwnerUserId.Equals(callerUserId)
                                    || selector.Members.Any(member => member.ProfileUserId.Equals(callerUserId)),
                        cancellationToken);

            public Task<ProfileSelector?> FindSelectorAsync(Guid selectorId, CancellationToken cancellationToken)
                => QuerySelectors()
                    .SingleOrDefaultAsync(selector => selector.Id.Equals(selectorId), cancellationToken);

            public Task<User?> FindUserPolicyAsync(Guid userId, CancellationToken cancellationToken)
                => _dbContext.Users
                    .Include(user => user.Permissions)
                    .Include(user => user.Preferences)
                    .Include(user => user.AccessSchedules)
                    .SingleOrDefaultAsync(user => user.Id.Equals(userId), cancellationToken);

            public Task<ProfileSelectorPlaybackStopReport?> FindPlaybackReportAsync(
                string reportKey,
                CancellationToken cancellationToken)
                => _dbContext.ProfileSelectorPlaybackStopReports
                    .SingleOrDefaultAsync(report => report.ReportKey == reportKey, cancellationToken);

            public Task<bool> HasUnclassifiedPlaybackReportAsync(
                Guid switchId,
                CancellationToken cancellationToken)
                => _dbContext.ProfileSelectorPlaybackStopReports.AnyAsync(
                    report => report.SwitchId.Equals(switchId)
                              && (report.State == ProfileSelectorPlaybackStopReportState.Processing
                                  || report.State == ProfileSelectorPlaybackStopReportState.Failed),
                    cancellationToken);

            public Task<int> CountRetainedOperationsAsync(
                Guid callerUserId,
                string deviceId,
                DateTime now,
                CancellationToken cancellationToken)
                => _dbContext.ProfileSelectorSwitchOperations.CountAsync(
                    operation => operation.CallerUserId.Equals(callerUserId)
                                 && operation.DeviceId == deviceId
                                 && operation.RetainUntilUtc > now,
                    cancellationToken);

            public Task<int> CountPlaybackReportsAsync(Guid switchId, CancellationToken cancellationToken)
                => _dbContext.ProfileSelectorPlaybackStopReports.CountAsync(
                    report => report.SwitchId.Equals(switchId),
                    cancellationToken);

            public async Task RunMaintenanceAsync(DateTime now, int batchSize, CancellationToken cancellationToken)
            {
                var expiredOperations = await _dbContext.ProfileSelectorSwitchOperations
                    .Where(operation => operation.State == ProfileSelectorSwitchOperationState.Prepared
                                        && operation.PreparedExpiresUtc <= now)
                    .OrderBy(operation => operation.PreparedExpiresUtc)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var operation in expiredOperations)
                {
                    operation.State = ProfileSelectorSwitchOperationState.Expired;
                    operation.ActiveDeviceId = null;
                    operation.DateModifiedUtc = now;
                }

                if (expiredOperations.Count > 0)
                {
                    await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                var remainingBatch = batchSize - expiredOperations.Count;
                if (remainingBatch <= 0)
                {
                    return;
                }

                var purgeableOperations = await _dbContext.ProfileSelectorSwitchOperations
                    .Where(operation => operation.RetainUntilUtc <= now)
                    .OrderBy(operation => operation.RetainUntilUtc)
                    .Take(remainingBatch)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (purgeableOperations.Count == 0)
                {
                    return;
                }

                _dbContext.ProfileSelectorSwitchOperations.RemoveRange(purgeableOperations);
                await SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            public void AddOperation(ProfileSelectorSwitchOperation operation)
                => _dbContext.ProfileSelectorSwitchOperations.Add(operation);

            public void AddPlaybackReport(ProfileSelectorPlaybackStopReport report)
                => _dbContext.ProfileSelectorPlaybackStopReports.Add(report);

            public void AddAuthenticationDevice(Device device)
                => _dbContext.Devices.Add(device);

            public void AssertUserPolicyUnchanged(User user)
            {
                _dbContext.Entry(user).Property(entity => entity.RowVersion).IsModified = true;
                foreach (var permission in user.Permissions)
                {
                    _dbContext.Entry(permission).Property(entity => entity.RowVersion).IsModified = true;
                }

                foreach (var preference in user.Preferences)
                {
                    _dbContext.Entry(preference).Property(entity => entity.RowVersion).IsModified = true;
                }
            }

            public void RemoveOperation(ProfileSelectorSwitchOperation operation)
                => _dbContext.ProfileSelectorSwitchOperations.Remove(operation);

            public async Task SaveChangesAsync(CancellationToken cancellationToken)
            {
                try
                {
                    await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
                {
                    throw new ProfileSwitchPersistenceConflictException(ex);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    throw new ProfileSwitchPersistenceConflictException(ex);
                }
            }

            public async Task<IProfileSwitchTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
            {
                await _dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
                var connection = (SqliteConnection)_dbContext.Database.GetDbConnection();
                var transaction = connection.BeginTransaction(IsolationLevel.Serializable, deferred: false);
                var contextTransaction = await _dbContext.Database
                    .UseTransactionAsync(transaction, cancellationToken)
                    .ConfigureAwait(false);
                return new EfProfileSwitchTransaction(contextTransaction!);
            }

            public ValueTask DisposeAsync()
                => _dbContext.DisposeAsync();

            private IQueryable<ProfileSelector> QuerySelectors()
                => _dbContext.ProfileSelectors
                    .Include(selector => selector.Members)
                    .Include(selector => selector.DeviceStates);
        }

        private sealed class EfProfileSwitchTransaction : IProfileSwitchTransaction
        {
            private readonly IDbContextTransaction _transaction;

            public EfProfileSwitchTransaction(IDbContextTransaction transaction)
            {
                _transaction = transaction;
            }

            public Task CommitAsync(CancellationToken cancellationToken)
                => _transaction.CommitAsync(cancellationToken);

            public ValueTask DisposeAsync()
                => _transaction.DisposeAsync();
        }
    }
}
