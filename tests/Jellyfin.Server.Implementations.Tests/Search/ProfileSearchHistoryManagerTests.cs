using System;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.Search;
using MediaBrowser.Controller.Search;
using MediaBrowser.Model.Explore;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Search;

public sealed class ProfileSearchHistoryManagerTests
{
    [Theory]
    [InlineData(SearchHistoryUpdateRequestDto.MaxSearchTermLength, SearchHistoryRecordResult.EmptyTerm)]
    [InlineData(SearchHistoryUpdateRequestDto.MaxSearchTermLength + 1, SearchHistoryRecordResult.RawTermTooLong)]
    public async Task RecordSearchAsync_EnforcesRawInputBoundaryBeforeDatabase(int length, SearchHistoryRecordResult expectedResult)
    {
        var dbContextFactory = new Mock<IDbContextFactory<JellyfinDbContext>>(MockBehavior.Strict);
        var manager = new ProfileSearchHistoryManager(dbContextFactory.Object);

        var result = await manager.RecordSearchAsync(
            Guid.NewGuid(),
            Guid.NewGuid(),
            new string(' ', length),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedResult, result);
        dbContextFactory.VerifyNoOtherCalls();
    }
}
