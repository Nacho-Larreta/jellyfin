using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Item;

public sealed class ParentalRatingRepositoryTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public ParentalRatingRepositoryTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    public static TheoryData<ParentalRatingFilter, int?[]> RatedFilterCases => new()
    {
        { new ParentalRatingFilter("rated-only", true, null, null), [0, 7, 13, 14] },
        { new ParentalRatingFilter("bounded-zero", true, null, 0), [0] },
        { new ParentalRatingFilter("bounded-higher", true, null, 13), [0, 7, 13] },
        { new ParentalRatingFilter("unrated-only", false, null, null), [null] },
        { new ParentalRatingFilter("unlimited-rated", true, null, null), [0, 7, 13, 14] },
        { new ParentalRatingFilter("legacy-min-only", null, 7, null), [null, 7, 13, 14] },
        { new ParentalRatingFilter("legacy-max-only", null, null, 13), [null, 0, 7, 13] }
    };

    [Theory]
    [MemberData(nameof(RatedFilterCases))]
    public async Task GetItemList_ComposesParentalRatingFiltersInSqlite(
        ParentalRatingFilter filter,
        int?[] expectedRatings)
    {
        var dbContextFactory = _factory.Services.GetRequiredService<IDbContextFactory<JellyfinDbContext>>();
        var repository = _factory.Services.GetRequiredService<IItemRepository>();
        var items = CreateItems();

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        dbContext.BaseItems.AddRange(items);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        try
        {
            var query = new InternalItemsQuery
            {
                GroupByPresentationUniqueKey = false,
                HasParentalRating = filter.HasParentalRating,
                ItemIds = items.Select(item => item.Id).ToArray(),
                MinParentalRating = filter.MinimumRating is null
                    ? null
                    : new ParentalRatingScore(filter.MinimumRating.Value, null),
                MaxParentalRating = filter.MaximumRating is null
                    ? null
                    : new ParentalRatingScore(filter.MaximumRating.Value, null)
            };

            var actualRatings = repository.GetItemList(query)
                .Select(item => item.InheritedParentalRatingValue)
                .OrderBy(rating => rating)
                .ToArray();

            Assert.Equal(expectedRatings, actualRatings);
        }
        finally
        {
            await dbContext.BaseItems
                .Where(item => items.Select(seed => seed.Id).Contains(item.Id))
                .ExecuteDeleteAsync(TestContext.Current.CancellationToken);
        }
    }

    private static BaseItemEntity[] CreateItems()
        => new int?[] { null, 0, 7, 13, 14 }
            .Select(rating => new BaseItemEntity
            {
                Id = Guid.NewGuid(),
                Type = typeof(Movie).FullName!,
                InheritedParentalRatingValue = rating
            })
            .ToArray();

    public sealed record ParentalRatingFilter(
        string Scenario,
        bool? HasParentalRating,
        int? MinimumRating,
        int? MaximumRating);
}
