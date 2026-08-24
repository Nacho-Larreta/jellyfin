using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Item;
using Jellyfin.Server.Implementations.Migrations;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Search;

public sealed class SearchPersistenceTests : IClassFixture<JellyfinApplicationFactory>
{
    private const string SearchHistoryMigration = "20260503000000_AddProfileSearchHistory";
    private const string PreviousMigration = "20260426230527_AddProfileSelector";
    private readonly JellyfinApplicationFactory _factory;

    public SearchPersistenceTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SearchRanking_IsTranslatedAndOrderedByRealSqlite()
    {
        var dbContextFactory = _factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        var itemIds = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        dbContext.BaseItems.AddRange(
            CreateItem(itemIds[0], "spider man"),
            CreateItem(itemIds[1], "spiderman"),
            CreateItem(itemIds[2], "spider man returns"),
            CreateItem(itemIds[3], "spiderman returns"),
            CreateItem(itemIds[4], "the spider man story"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        try
        {
            var orderedNames = await dbContext.BaseItems
                .AsNoTracking()
                .Where(item => itemIds.Contains(item.Id))
                .OrderBy(OrderMapper.MapSearchRelevanceOrder("Spider-Man"))
                .Select(item => item.CleanName)
                .ToArrayAsync(TestContext.Current.CancellationToken);

            Assert.Equal(
                new string?[] { "spider man", "spiderman", "spider man returns", "spiderman returns", "the spider man story" },
                orderedNames);
        }
        finally
        {
            await dbContext.BaseItems
                .Where(item => itemIds.Contains(item.Id))
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task PunctuationOnlySearch_DoesNotMatchAnySqliteItem()
    {
        var dbContextFactory = _factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        var itemId = Guid.NewGuid();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        dbContext.BaseItems.Add(CreateItem(itemId, "a searchable item"));
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        try
        {
            var repository = _factory.Services.GetRequiredService<IItemRepository>();
            var results = repository.GetItemList(new InternalItemsQuery { SearchTerm = "---" });

            Assert.Empty(results);
        }
        finally
        {
            await dbContext.BaseItems
                .Where(item => item.Id.Equals(itemId))
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task SearchHistoryMigration_UpgradesPriorFilePreservesDataAndReopens()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"jellyfin-search-migration-{Guid.NewGuid():N}.db");
        var existingUser = new User("migration-probe", "default", "default");

        try
        {
            await using (var previousContext = CreateFileContext(databasePath))
            {
                await previousContext.Database.GetService<IMigrator>()
                    .MigrateAsync(PreviousMigration, TestContext.Current.CancellationToken);
                previousContext.Users.Add(existingUser);
                await previousContext.SaveChangesAsync(TestContext.Current.CancellationToken);
                Assert.DoesNotContain(SearchHistoryMigration, await previousContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
            }

            await using (var upgradedContext = CreateFileContext(databasePath))
            {
                var migrator = upgradedContext.Database.GetService<IMigrator>();
                await migrator.MigrateAsync(SearchHistoryMigration, TestContext.Current.CancellationToken);
                await migrator.MigrateAsync(SearchHistoryMigration, TestContext.Current.CancellationToken);

                Assert.Contains(SearchHistoryMigration, await upgradedContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
                Assert.True(await upgradedContext.Users.AnyAsync(user => user.Id.Equals(existingUser.Id), TestContext.Current.CancellationToken));
                Assert.Equal(0, await upgradedContext.ProfileSearchHistoryEntries.CountAsync(TestContext.Current.CancellationToken));
            }

            await using var reopenedContext = CreateFileContext(databasePath);
            Assert.Contains(SearchHistoryMigration, await reopenedContext.Database.GetAppliedMigrationsAsync(TestContext.Current.CancellationToken));
            Assert.True(await reopenedContext.Users.AnyAsync(user => user.Id.Equals(existingUser.Id), TestContext.Current.CancellationToken));
            Assert.Equal(0, await reopenedContext.ProfileSearchHistoryEntries.CountAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(databasePath);
        }
    }

    private static BaseItemEntity CreateItem(Guid id, string cleanName)
        => new()
        {
            Id = id,
            Type = "Movie",
            CleanName = cleanName
        };

    private static JellyfinDbContext CreateFileContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite(
                $"Data Source={databasePath}",
                sqlite => sqlite.MigrationsAssembly(typeof(AddProfileSearchHistory).Assembly.FullName))
            .Options;
        return new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }
}
