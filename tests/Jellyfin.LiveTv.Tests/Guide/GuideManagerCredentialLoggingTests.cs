using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Jellyfin.LiveTv.Guide;
using Jellyfin.LiveTv.Tests.Logging;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests.Guide;

public class GuideManagerCredentialLoggingTests
{
    [Fact]
    public async Task PreCacheImages_CredentialBearingUrlNeverReachesLog()
    {
        const string Marker = "GUIDE_IMAGE_TOKEN_MARKER";
        var logger = new CapturingLogger<GuideManager>();
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager
            .Setup(manager => manager.ConvertImageToLocal(
                It.IsAny<BaseItem>(),
                It.IsAny<ItemImageInfo>(),
                It.IsAny<int>(),
                It.IsAny<bool>()))
            .ThrowsAsync(new HttpRequestException("image unavailable"));
        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject<ILogger<GuideManager>>(logger);
        fixture.Inject(libraryManager.Object);
        var manager = fixture.Create<GuideManager>();
        var program = new LiveTvProgram
        {
            Id = Guid.NewGuid(),
            EndDate = DateTime.UtcNow.AddMinutes(-1),
            ImageInfos =
            [
                new ItemImageInfo
                {
                    Path = "https://images.example.test/poster.jpg?token=" + Marker,
                    Type = ImageType.Primary
                }
            ]
        };

        await manager.PreCacheImages(new List<BaseItem> { program }, DateTime.UtcNow);

        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(Marker, message, StringComparison.Ordinal));
        Assert.All(
            logger.Entries,
            entry =>
            {
                Assert.Equal(
                    ["ImageType", "ProgramId", "{OriginalFormat}"],
                    entry.Properties.Keys.Order(StringComparer.Ordinal));
                Assert.Equal(ImageType.Primary, entry.Properties["ImageType"]);
                Assert.Equal(program.Id, entry.Properties["ProgramId"]);
                Assert.DoesNotContain(Marker, JsonSerializer.Serialize(entry.Properties), StringComparison.Ordinal);
            });
        Assert.Null(logger.Entries[0].Exception);
        Assert.IsType<HttpRequestException>(logger.Entries[1].Exception);
    }
}
