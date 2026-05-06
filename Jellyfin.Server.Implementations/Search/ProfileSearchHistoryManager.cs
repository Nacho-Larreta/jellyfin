using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Search;
using MediaBrowser.Model.Explore;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Search
{
    /// <summary>
    /// Database-backed profile search history manager.
    /// </summary>
    public sealed partial class ProfileSearchHistoryManager : IProfileSearchHistoryManager
    {
        private const int MaxTermLength = 255;
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
        public async Task RecordSearchAsync(Guid ownerUserId, Guid profileUserId, string searchTerm, CancellationToken cancellationToken)
        {
            var displayTerm = NormalizeDisplayTerm(searchTerm);
            if (displayTerm.Length == 0)
            {
                return;
            }

            var normalizedTerm = displayTerm.ToLowerInvariant();
            var now = DateTime.UtcNow;

            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var existing = await dbContext.ProfileSearchHistoryEntries
                .FirstOrDefaultAsync(
                    entry => entry.OwnerUserId.Equals(ownerUserId)
                             && entry.ProfileUserId.Equals(profileUserId)
                             && entry.SearchTermNormalized == normalizedTerm,
                    cancellationToken)
                .ConfigureAwait(false);

            if (existing is null)
            {
                dbContext.ProfileSearchHistoryEntries.Add(new ProfileSearchHistoryEntry
                {
                    OwnerUserId = ownerUserId,
                    ProfileUserId = profileUserId,
                    SearchTerm = displayTerm,
                    SearchTermNormalized = normalizedTerm,
                    HitCount = 1,
                    DateCreatedUtc = now,
                    LastSearchedUtc = now
                });
            }
            else
            {
                existing.SearchTerm = displayTerm;
                existing.HitCount++;
                existing.LastSearchedUtc = now;
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
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

        private static string NormalizeDisplayTerm(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm))
            {
                return string.Empty;
            }

            var normalized = WhitespaceRegex().Replace(searchTerm.Trim(), " ");
            return normalized.Length <= MaxTermLength ? normalized : normalized[..MaxTermLength];
        }

        [GeneratedRegex(@"\s+")]
        private static partial Regex WhitespaceRegex();
    }
}
