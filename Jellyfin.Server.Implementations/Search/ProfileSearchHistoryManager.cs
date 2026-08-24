using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Search;
using MediaBrowser.Model.Explore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Search
{
    /// <summary>
    /// Database-backed profile search history manager.
    /// </summary>
    public sealed class ProfileSearchHistoryManager : IProfileSearchHistoryManager
    {
        private const int SqliteUniqueConstraintError = 2067;
        private const int MaxHistoryLimit = 50;
        private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSearchHistoryManager"/> class.
        /// </summary>
        /// <param name="dbContextFactory">The database context factory.</param>
        public ProfileSearchHistoryManager(IDbContextFactory<JellyfinDbContext> dbContextFactory)
        {
            _dbContextFactory = dbContextFactory;
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<SearchHistoryEntryDto>> GetHistoryAsync(Guid ownerUserId, Guid profileUserId, int limit, CancellationToken cancellationToken)
        {
            limit = Math.Clamp(limit, 1, MaxHistoryLimit);

            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            return await dbContext.ProfileSearchHistoryEntries
                .AsNoTracking()
                .Where(entry => entry.OwnerUserId.Equals(ownerUserId) && entry.ProfileUserId.Equals(profileUserId))
                .OrderByDescending(entry => entry.LastSearchedUtc)
                .Take(limit)
                .Select(entry => new SearchHistoryEntryDto
                {
                    SearchTerm = entry.SearchTerm,
                    HitCount = entry.HitCount,
                    LastSearchedUtc = entry.LastSearchedUtc
                })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<SearchHistoryRecordResult> RecordSearchAsync(Guid ownerUserId, Guid profileUserId, string searchTerm, CancellationToken cancellationToken)
        {
            if (searchTerm.Length > SearchHistoryUpdateRequestDto.MaxSearchTermLength)
            {
                return SearchHistoryRecordResult.RawTermTooLong;
            }

            var displayTerm = SearchTermNormalizer.NormalizeForDisplay(searchTerm);
            var normalizedTerm = SearchTermNormalizer.NormalizeForLookup(displayTerm);
            if (normalizedTerm.Length == 0)
            {
                return SearchHistoryRecordResult.EmptyTerm;
            }

            if (displayTerm.Length > SearchTermNormalizer.MaxHistoryTermLength)
            {
                return SearchHistoryRecordResult.TermTooLong;
            }

            var now = DateTime.UtcNow;

            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            if (await UpdateExistingEntryAsync(dbContext, ownerUserId, profileUserId, displayTerm, normalizedTerm, now, cancellationToken).ConfigureAwait(false) > 0)
            {
                return SearchHistoryRecordResult.Recorded;
            }

            var newEntry = new ProfileSearchHistoryEntry
            {
                OwnerUserId = ownerUserId,
                ProfileUserId = profileUserId,
                SearchTerm = displayTerm,
                SearchTermNormalized = normalizedTerm,
                HitCount = 1,
                DateCreatedUtc = now,
                LastSearchedUtc = now
            };
            dbContext.ProfileSearchHistoryEntries.Add(newEntry);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException exception) when (IsUniqueConstraintViolation(exception))
            {
                dbContext.Entry(newEntry).State = EntityState.Detached;
                var updatedEntries = await UpdateExistingEntryAsync(
                    dbContext,
                    ownerUserId,
                    profileUserId,
                    displayTerm,
                    normalizedTerm,
                    now,
                    cancellationToken).ConfigureAwait(false);
                if (updatedEntries == 0)
                {
                    throw;
                }
            }

            return SearchHistoryRecordResult.Recorded;
        }

        /// <inheritdoc />
        public async Task ClearHistoryAsync(Guid ownerUserId, Guid profileUserId, CancellationToken cancellationToken)
        {
            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            await dbContext.ProfileSearchHistoryEntries
                .Where(entry => entry.OwnerUserId.Equals(ownerUserId) && entry.ProfileUserId.Equals(profileUserId))
                .ExecuteDeleteAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        private static Task<int> UpdateExistingEntryAsync(
            JellyfinDbContext dbContext,
            Guid ownerUserId,
            Guid profileUserId,
            string displayTerm,
            string normalizedTerm,
            DateTime now,
            CancellationToken cancellationToken)
            => dbContext.ProfileSearchHistoryEntries
                .Where(entry => entry.OwnerUserId.Equals(ownerUserId)
                                && entry.ProfileUserId.Equals(profileUserId)
                                && entry.SearchTermNormalized == normalizedTerm)
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(entry => entry.SearchTerm, displayTerm)
                        .SetProperty(entry => entry.HitCount, entry => entry.HitCount + 1)
                        .SetProperty(entry => entry.LastSearchedUtc, now),
                    cancellationToken);

        private static bool IsUniqueConstraintViolation(DbUpdateException exception)
            => exception.InnerException is SqliteException { SqliteExtendedErrorCode: SqliteUniqueConstraintError };
    }
}
