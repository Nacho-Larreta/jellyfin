using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.LiveTv.Tests.Logging;
using Jellyfin.LiveTv.TunerHosts;
using MediaBrowser.Model.LiveTv;
using Moq;
using Moq.Protected;
using Xunit;

namespace Jellyfin.LiveTv.Tests.TunerHosts;

public class M3uParserLoggingTests
{
    [Fact]
    public async Task Parse_RejectedCredentialBearingPathNeverReachesLog()
    {
        const string Marker = "M3U_PATH_CREDENTIAL_MARKER";
        const string Playlist = "#EXTM3U\n#EXTINF:-1,Unsafe\ncredential://viewer:" + Marker + "@example.test/channel\n";
        var messageHandler = new Mock<HttpMessageHandler>();
        messageHandler.Protected()
            .Setup<Task<HttpResponseMessage>>("SendAsync", ItExpr.IsAny<HttpRequestMessage>(), ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                Content = new StringContent(Playlist)
            });
        using var httpClient = new HttpClient(messageHandler.Object);
        var httpClientFactory = new Mock<IHttpClientFactory>();
        httpClientFactory.Setup(factory => factory.CreateClient(It.IsAny<string>())).Returns(httpClient);
        var logger = new CapturingLogger();
        var parser = new M3uParser(logger, httpClientFactory.Object);
        var tuner = new TunerHostInfo
        {
            Id = "tuner-id",
            Url = "https://example.test/list.m3u"
        };

        var channels = await parser.Parse(tuner, "prefix", TestContext.Current.CancellationToken);

        Assert.Empty(channels);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("Skipping M3U channel entry", message, System.StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, message, System.StringComparison.Ordinal);
        var entry = Assert.Single(logger.Entries);
        Assert.Single(entry.Properties);
        Assert.True(entry.Properties.ContainsKey("{OriginalFormat}"));
        Assert.Null(entry.Exception);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(entry.Properties), System.StringComparison.Ordinal);
    }
}
