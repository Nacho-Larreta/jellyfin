using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Tests.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using Moq;
using Xunit;

namespace Jellyfin.LiveTv.Tests;

public class LiveTvMediaSourceProviderLoggingTests
{
    [Fact]
    public async Task GetMediaSources_CredentialBearingPathNeverReachesLog()
    {
        const string Marker = "MEDIA_SOURCE_PATH_TOKEN_MARKER";
        var activeRecording = new ActiveRecordingInfo { Id = "recording-id", Path = "recording-path" };
        var recordingsManager = new Mock<IRecordingsManager>();
        recordingsManager
            .Setup(manager => manager.GetActiveRecordingInfo("recording-path"))
            .Returns(activeRecording);
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager
            .Setup(manager => manager.GetRecordingStreamMediaSources(activeRecording, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<MediaSourceInfo>
            {
                new()
                {
                    Path = "https://viewer:" + Marker + "@example.test/channel?token=" + Marker,
                    Protocol = MediaProtocol.Http
                }
            });
        var item = new Mock<BaseItem>();
        item.SetupGet(candidate => candidate.SourceType).Returns(SourceType.LiveTV);
        item.SetupGet(candidate => candidate.Path).Returns("recording-path");
        var logger = new CapturingLogger<LiveTvMediaSourceProvider>();
        var provider = new LiveTvMediaSourceProvider(
            logger,
            Mock.Of<IServerApplicationHost>(),
            recordingsManager.Object,
            mediaSourceManager.Object,
            Mock.Of<ILibraryManager>(),
            Array.Empty<ILiveTvService>());

        var mediaSources = (await provider.GetMediaSources(item.Object, TestContext.Current.CancellationToken)).ToList();

        var mediaSource = Assert.Single(mediaSources);
        Assert.Contains(Marker, mediaSource.Path, StringComparison.Ordinal);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("Prepared 1 Live TV media sources", message, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, message, StringComparison.Ordinal);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(
            ["MediaSourceCount", "{OriginalFormat}"],
            entry.Properties.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(1, entry.Properties["MediaSourceCount"]);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(entry.Properties), StringComparison.Ordinal);
        Assert.DoesNotContain(entry.Properties.Values, value => value is MediaSourceInfo or IEnumerable<MediaSourceInfo>);
    }
}
