using System;
using AutoFixture;
using AutoFixture.AutoMoq;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Item;
using Jellyfin.Server.Implementations.Search;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Data
{
    public class SearchPunctuationTests
    {
        private readonly IFixture _fixture;
        private readonly BaseItemRepository _repo;

        public SearchPunctuationTests()
        {
            var appHost = new Mock<MediaBrowser.Controller.IServerApplicationHost>();
            appHost.Setup(x => x.ExpandVirtualPath(It.IsAny<string>()))
                .Returns((string x) => x);
            appHost.Setup(x => x.ReverseVirtualPath(It.IsAny<string>()))
                .Returns((string x) => x);

            var configSection = new Mock<IConfigurationSection>();
            configSection
                .SetupGet(x => x[It.Is<string>(s => s == MediaBrowser.Controller.Extensions.ConfigurationExtensions.SqliteCacheSizeKey)])
                .Returns("0");
            var config = new Mock<IConfiguration>();
            config
                .Setup(x => x.GetSection(It.Is<string>(s => s == MediaBrowser.Controller.Extensions.ConfigurationExtensions.SqliteCacheSizeKey)))
                .Returns(configSection.Object);

            _fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
            _fixture.Inject(appHost.Object);
            _fixture.Inject(config.Object);

            _repo = _fixture.Create<BaseItemRepository>();
        }

        [Fact]
        public void CleanName_keeps_punctuation_and_search_without_punctuation_passes()
        {
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = "Mr. Robot"
            };

            series.SortName = "Mr. Robot";

            var entity = _repo.Map(series);
            Assert.Equal("mr robot", entity.CleanName);

            var searchTerm = "Mr Robot".ToLowerInvariant();

            Assert.Contains(searchTerm, entity.CleanName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Spider-Man: Homecoming", "spider man homecoming")]
        [InlineData("Beyoncé — Live!", "beyonce live")]
        [InlineData("Hello, World!", "hello world")]
        [InlineData("(The) Good, the Bad & the Ugly", "the good the bad the ugly")]
        [InlineData("Wall-E", "wall e")]
        [InlineData("No. 1: The Beginning", "no 1 the beginning")]
        [InlineData("Café-au-lait", "cafe au lait")]
        [InlineData("Pokémon", "pokemon")]
        [InlineData("Pokèmon", "pokemon")]
        public void CleanName_normalizes_various_punctuation(string title, string expectedClean)
        {
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = title
            };

            series.SortName = title;

            var entity = _repo.Map(series);

            Assert.Equal(expectedClean, entity.CleanName);

            // Ensure a search term without punctuation would match
            var searchTerm = expectedClean;
            Assert.Contains(searchTerm, entity.CleanName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Face/Off", "face off")]
        [InlineData("V/H/S", "v h s")]
        public void CleanName_normalizes_titles_withslashes(string title, string expectedClean)
        {
            var series = new Series
            {
                Id = Guid.NewGuid(),
                Name = title
            };

            series.SortName = title;

            var entity = _repo.Map(series);

            Assert.Equal(expectedClean, entity.CleanName);

            // Ensure a search term without punctuation would match
            var searchTerm = expectedClean;
            Assert.Contains(searchTerm, entity.CleanName ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("Spider-Man", "spiderman")]
        [InlineData("Spider Man", "spiderman")]
        [InlineData("Wall-E", "walle")]
        [InlineData("Pokémon", "pokemon")]
        [InlineData("Pokèmon", "pokemon")]
        [InlineData("---", "")]
        public void CompactName_removes_diacritics_and_separators(string title, string expectedCompact)
        {
            Assert.Equal(expectedCompact, SearchTermNormalizer.NormalizeForLookup(title));
        }

        [Theory]
        [InlineData("", "")]
        [InlineData("  ", "")]
        [InlineData("---", "---")]
        [InlineData("  Spider\t  Man  ", "Spider Man")]
        public void DisplayName_collapses_whitespace_without_silent_truncation(string searchTerm, string expectedDisplay)
        {
            Assert.Equal(expectedDisplay, SearchTermNormalizer.NormalizeForDisplay(searchTerm));
        }

        [Theory]
        [InlineData("spiderman", "spiderman", 0)]
        [InlineData("spiderman", "spider man", 1)]
        [InlineData("spider man", "spiderman", 1)]
        [InlineData("pokèmon", "pokemon", 0)]
        [InlineData("pokemon", "pokemon the movie", 2)]
        [InlineData("walle", "wall e", 1)]
        public void Search_relevance_prioritizes_normalized_and_separator_insensitive_matches(
            string searchTerm,
            string cleanName,
            int expectedRank)
        {
            var rank = OrderMapper.MapSearchRelevanceOrder(searchTerm).Compile();
            var entity = new BaseItemEntity
            {
                Id = Guid.NewGuid(),
                Type = nameof(Series),
                CleanName = cleanName
            };

            Assert.Equal(expectedRank, rank(entity));
        }
    }
}
