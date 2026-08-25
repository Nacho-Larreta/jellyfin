using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AutoFixture;
using AutoFixture.AutoMoq;
using Emby.Server.Implementations.Library;
using Jellyfin.Server.Implementations.Tests.Logging;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class MediaSourceManagerCredentialLoggingTests
{
    [Fact]
    public async Task OpenLiveStream_CredentialBearingMediaPathNeverReachesLog()
    {
        const string Marker = "OPEN_MEDIA_SOURCE_PATH_MARKER";
        var logger = new CapturingLogger<MediaSourceManager>();
        var fixture = new Fixture().Customize(new AutoMoqCustomization { ConfigureMembers = true });
        fixture.Inject<ILogger<MediaSourceManager>>(logger);
        var manager = fixture.Create<MediaSourceManager>();
        var mediaSource = new MediaSourceInfo
        {
            Id = "source-id",
            LiveStreamId = "stream-id",
            Path = "https://viewer:" + Marker + "@example.test/channel?token=" + Marker,
            Protocol = MediaProtocol.Http,
            SupportsDirectStream = false,
            SupportsProbing = false,
            MediaStreams =
            [
                new MediaStream
                {
                    Index = 0,
                    Type = MediaStreamType.Video
                }
            ]
        };
        var liveStream = new Mock<ILiveStream>();
        liveStream.SetupProperty(stream => stream.MediaSource, mediaSource);
        var provider = new Mock<IMediaSourceProvider>();
        provider
            .Setup(candidate => candidate.OpenMediaSource(
                "key-id",
                It.IsAny<List<ILiveStream>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveStream.Object);
        manager.AddParts([provider.Object]);
        var providerPrefix = provider.Object.GetType().FullName!.GetMD5().ToString("N", CultureInfo.InvariantCulture);
        var request = new LiveStreamRequest
        {
            OpenToken = string.Concat(providerPrefix, "_key-id")
        };

        var response = await manager.OpenLiveStream(request, TestContext.Current.CancellationToken);

        Assert.Contains(Marker, response.MediaSource.Path, StringComparison.Ordinal);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("Live stream opened using Http with 1 media streams", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, message, StringComparison.Ordinal);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(
            ["MediaStreamCount", "Protocol", "{OriginalFormat}"],
            entry.Properties.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(MediaProtocol.Http, entry.Properties["Protocol"]);
        Assert.Equal(1, entry.Properties["MediaStreamCount"]);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(entry.Properties), StringComparison.Ordinal);
        Assert.DoesNotContain(entry.Properties.Values, value => value is MediaSourceInfo or IEnumerable<MediaSourceInfo>);
    }
}
