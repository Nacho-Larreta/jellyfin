using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Naming.Common;
using Jellyfin.Server.Implementations.Tests.Logging;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library.LibraryManager;

public class CredentialLogBoundaryTests
{
    [Fact]
    public async Task ConvertImageToLocal_FailedCredentialBearingUrlNeverReachesLog()
    {
        const string Marker = "IMAGE_URL_TOKEN_MARKER";
        var logger = new CapturingLogger<Emby.Server.Implementations.Library.LibraryManager>();
        var loggerFactory = new Mock<ILoggerFactory>();
        loggerFactory
            .Setup(factory => factory.CreateLogger(It.IsAny<string>()))
            .Returns(logger);
        var providerManager = new Mock<IProviderManager>();
        providerManager
            .Setup(manager => manager.SaveImage(
                It.IsAny<BaseItem>(),
                It.IsAny<string>(),
                It.IsAny<ImageType>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden));
        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Register(() => new NamingOptions());
        var configurationManager = fixture.Freeze<Mock<IServerConfigurationManager>>();
        configurationManager.SetupGet(manager => manager.Configuration).Returns(new ServerConfiguration());
        fixture.Inject(loggerFactory.Object);
        fixture.Inject(new Lazy<IProviderManager>(() => providerManager.Object));
        var libraryManager = fixture.Create<Emby.Server.Implementations.Library.LibraryManager>();
        var item = new Movie { Id = Guid.NewGuid() };
        var image = new ItemImageInfo
        {
            Path = "https://images.example.test/poster.jpg?token=" + Marker,
            Type = ImageType.Primary
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => libraryManager.ConvertImageToLocal(item, image, 0, false));

        Assert.Equal(2, logger.Messages.Count);
        Assert.All(logger.Messages, message => Assert.DoesNotContain(Marker, message, StringComparison.Ordinal));
        Assert.All(
            logger.Entries,
            entry =>
            {
                Assert.Equal(
                    ["ImageType", "ItemId", "{OriginalFormat}"],
                    entry.Properties.Keys.Order(StringComparer.Ordinal));
                Assert.Equal(ImageType.Primary, entry.Properties["ImageType"]);
                Assert.Equal(item.Id, entry.Properties["ItemId"]);
                Assert.DoesNotContain(Marker, JsonSerializer.Serialize(entry.Properties), StringComparison.Ordinal);
            });
        Assert.Null(logger.Entries[0].Exception);
        Assert.IsType<HttpRequestException>(logger.Entries[1].Exception);
    }
}
