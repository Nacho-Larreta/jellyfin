using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.MediaEncoding.Transcoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.Transcoding;

public sealed class TranscodeManagerKillRequestTests
{
    [Theory]
    [InlineData("device-a", "play-a", true)]
    [InlineData("device-a", "play-b", false)]
    [InlineData("device-b", "play-a", false)]
    [InlineData("device-b", "play-b", false)]
    public void MatchesKillRequest_RequiresCanonicalDeviceAndPlaySession(
        string requestedDeviceId,
        string requestedPlaySessionId,
        bool expected)
    {
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            DeviceId = "device-a",
            PlaySessionId = "play-a"
        };

        Assert.Equal(
            expected,
            TranscodeManager.MatchesKillRequest(job, requestedDeviceId, requestedPlaySessionId));
    }

    [Fact]
    public void MatchesKillRequest_WithoutPlaySessionStillRequiresCanonicalDevice()
    {
        using var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            DeviceId = "device-a",
            PlaySessionId = "play-a"
        };

        Assert.True(TranscodeManager.MatchesKillRequest(job, "device-a", null));
        Assert.False(TranscodeManager.MatchesKillRequest(job, "device-b", null));
    }
}
