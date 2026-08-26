using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.ProfileSelectorsDtos;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Controller.Security;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers;

public sealed class ProfileSwitchControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public ProfileSwitchControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PrepareCommitStatusAndAbort_AreAuthenticatedDurableAndReplayable()
    {
        var context = await Users.ProfileSelectorApiTestContext.CreateAsync(_factory);
        var switchId = Guid.NewGuid();
        var prepareUrl = $"ProfileSelectors/Current/Switches/{switchId.ToString("N", CultureInfo.InvariantCulture)}/Prepare";

        using (var oversizedPlaybackResponse = await context.OwnerClient.PostAsJsonAsync(
                   $"ProfileSelectors/Current/Switches/{Guid.NewGuid():N}/PlaybackStopped",
                   new
                   {
                       ItemId = Guid.NewGuid(),
                       PlaySessionId = "play-session",
                       PositionTicks = 1,
                       Padding = new string('x', 20_000)
                   },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, oversizedPlaybackResponse.StatusCode);
        }

        using (var mismatchedDeviceClient = CreateAuthenticatedClient(context, context.OwnerAccessToken, "spoofed-device", includeDeviceId: true))
        using (var mismatchedDeviceResponse = await mismatchedDeviceClient.PostAsJsonAsync(
                   prepareUrl,
                   new ProfileSwitchPrepareRequestDto { TargetProfileUserId = context.ProfileId },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, mismatchedDeviceResponse.StatusCode);
        }

        var headerlessSwitchId = Guid.NewGuid();
        using (var headerlessDeviceClient = CreateAuthenticatedClient(context, context.OwnerAccessToken, string.Empty, includeDeviceId: false))
        {
            using var headerlessPrepareResponse = await headerlessDeviceClient.PostAsJsonAsync(
                $"ProfileSelectors/Current/Switches/{headerlessSwitchId:N}/Prepare",
                new ProfileSwitchPrepareRequestDto { TargetProfileUserId = context.ProfileId },
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, headerlessPrepareResponse.StatusCode);
            using var headerlessAbortResponse = await headerlessDeviceClient.DeleteAsync(
                $"ProfileSelectors/Current/Switches/{headerlessSwitchId:N}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, headerlessAbortResponse.StatusCode);
        }

        using (var anonymousClient = _factory.CreateClient())
        using (var anonymousResponse = await anonymousClient.PostAsJsonAsync(
                   prepareUrl,
                   new ProfileSwitchPrepareRequestDto { TargetProfileUserId = context.ProfileId },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
        }

        using var prepareResponse = await context.OwnerClient.PostAsJsonAsync(
            prepareUrl,
            new ProfileSwitchPrepareRequestDto { TargetProfileUserId = context.ProfileId },
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, prepareResponse.StatusCode);
        var prepared = await prepareResponse.Content.ReadFromJsonAsync<ProfileSwitchResult>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchState.Prepared, prepared?.State);
        Assert.Null(prepared?.AuthenticationResult);

        var deviceManager = _factory.Services.GetRequiredService<IDeviceManager>();
        var canonicalCredential = Assert.Single(
            deviceManager.GetDevices(new Jellyfin.Data.Queries.DeviceQuery { AccessToken = context.OwnerAccessToken }).Items);
        var sessionManager = _factory.Services.GetRequiredService<ISessionManager>();
        var userManager = _factory.Services.GetRequiredService<MediaBrowser.Controller.Library.IUserManager>();
        var owner = userManager.GetUserById(context.OwnerId);
        Assert.NotNull(owner);
        var canonicalSession = await sessionManager.LogSessionActivity(
            canonicalCredential.AppName,
            canonicalCredential.AppVersion,
            canonicalCredential.DeviceId,
            canonicalCredential.DeviceName,
            "127.0.0.1",
            owner!);
        var playingItemId = Guid.NewGuid();
        canonicalSession.NowPlayingItem = new BaseItemDto { Id = playingItemId, Name = "Canonical playback" };
        canonicalSession.PlaySessionId = "canonical-play-session";
        using (var spoofedClient = CreateAuthenticatedClient(
                   context,
                   context.OwnerAccessToken,
                   canonicalCredential.DeviceId,
                   includeDeviceId: true,
                   clientName: "spoofed-client"))
        using (var playbackResponse = await spoofedClient.PostAsJsonAsync(
                   $"ProfileSelectors/Current/Switches/{switchId:N}/PlaybackStopped",
                   new ProfileSwitchPlaybackStopRequestDto
                   {
                       ItemId = playingItemId,
                       PlaySessionId = "canonical-play-session",
                       PositionTicks = 1
                   },
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, playbackResponse.StatusCode);
            var playbackResult = await playbackResponse.Content.ReadFromJsonAsync<ProfileSwitchPlaybackStopResult>(
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(ProfileSwitchPlaybackStopOutcome.Acknowledged, playbackResult?.Outcome);
            Assert.Null(canonicalSession.NowPlayingItem);
        }

        var otherCredential = await deviceManager.CreateDevice(
            new Device(
                context.OwnerId,
                "Jellyfin.Server Integration Tests",
                "10.8.0",
                "Apple II",
                "69420"));
        try
        {
            using var otherCredentialClient = CreateAuthenticatedClient(
                context,
                otherCredential.AccessToken,
                "69420",
                includeDeviceId: true);
            using var otherCredentialResponse = await otherCredentialClient.GetAsync(
                $"ProfileSelectors/Current/Switches/{switchId:N}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, otherCredentialResponse.StatusCode);
        }
        finally
        {
            await deviceManager.DeleteDevice(otherCredential);
        }

        using (var beforeCommitResponse = await context.OwnerClient.GetAsync(
                   "ProfileSelectors/Current",
                   TestContext.Current.CancellationToken))
        {
            var beforeCommit = await beforeCommitResponse.Content.ReadFromJsonAsync<ProfileSelectorDto>(
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Null(beforeCommit?.CurrentDeviceProfileUserId);
        }

        using var commitResponse = await context.OwnerClient.PostAsync(
            $"ProfileSelectors/Current/Switches/{switchId.ToString("N", CultureInfo.InvariantCulture)}/Commit",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, commitResponse.StatusCode);
        var committed = await commitResponse.Content.ReadFromJsonAsync<ProfileSwitchResult>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchState.Committed, committed?.State);
        Assert.False(string.IsNullOrWhiteSpace(committed?.AuthenticationResult?.AccessToken));
        Assert.Equal(context.ProfileId, committed?.AuthenticationResult?.User.Id);

        using var statusResponse = await context.OwnerClient.GetAsync(
            $"ProfileSelectors/Current/Switches/{switchId.ToString("N", CultureInfo.InvariantCulture)}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, statusResponse.StatusCode);
        var status = await statusResponse.Content.ReadFromJsonAsync<ProfileSwitchResult>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(committed!.AuthenticationResult!.AccessToken, status!.AuthenticationResult!.AccessToken);

        using var commitReplayResponse = await context.OwnerClient.PostAsync(
            $"ProfileSelectors/Current/Switches/{switchId.ToString("N", CultureInfo.InvariantCulture)}/Commit",
            null,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, commitReplayResponse.StatusCode);
        var commitReplay = await commitReplayResponse.Content.ReadFromJsonAsync<ProfileSwitchResult>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(committed.AuthenticationResult.AccessToken, commitReplay!.AuthenticationResult!.AccessToken);

        using (var profileClient = context.CreateAuthenticatedClient(committed.AuthenticationResult.AccessToken))
        {
            var activeProfile = await AuthHelper.GetUserDtoAsync(profileClient);
            Assert.Equal(context.ProfileId, activeProfile.Id);
        }

        var abortSwitchId = Guid.NewGuid();
        using var secondPrepareResponse = await context.OwnerClient.PostAsJsonAsync(
            $"ProfileSelectors/Current/Switches/{abortSwitchId.ToString("N", CultureInfo.InvariantCulture)}/Prepare",
            new ProfileSwitchPrepareRequestDto { TargetProfileUserId = context.OwnerId },
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, secondPrepareResponse.StatusCode);

        using var abortResponse = await context.OwnerClient.DeleteAsync(
            $"ProfileSelectors/Current/Switches/{abortSwitchId.ToString("N", CultureInfo.InvariantCulture)}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, abortResponse.StatusCode);
        var aborted = await abortResponse.Content.ReadFromJsonAsync<ProfileSwitchResult>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchState.Aborted, aborted?.State);

        const string ApiKeyApp = "profile-switch-security-test";
        using (var createApiKeyResponse = await context.OwnerClient.PostAsync(
                   $"Auth/Keys?app={ApiKeyApp}",
                   null,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, createApiKeyResponse.StatusCode);
        }

        using var getApiKeysResponse = await context.OwnerClient.GetAsync("Auth/Keys", TestContext.Current.CancellationToken);
        var apiKeys = await getApiKeysResponse.Content.ReadFromJsonAsync<QueryResult<AuthenticationInfo>>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        var apiKey = Assert.Single(apiKeys!.Items, key => key.AppName == ApiKeyApp);
        try
        {
            using var apiKeyClient = CreateAuthenticatedClient(context, apiKey.AccessToken, "69420", includeDeviceId: true);
            using var apiKeyResponse = await apiKeyClient.PostAsJsonAsync(
                $"ProfileSelectors/Current/Switches/{Guid.NewGuid():N}/Prepare",
                new ProfileSwitchPrepareRequestDto { TargetProfileUserId = context.ProfileId },
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, apiKeyResponse.StatusCode);
        }
        finally
        {
            using var deleteApiKeyResponse = await context.OwnerClient.DeleteAsync(
                $"Auth/Keys/{apiKey.AccessToken}",
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, deleteApiKeyResponse.StatusCode);
        }
    }

    private static HttpClient CreateAuthenticatedClient(
        Users.ProfileSelectorApiTestContext context,
        string accessToken,
        string deviceId,
        bool includeDeviceId,
        string clientName = "Jellyfin.Server%20Integration%20Tests")
    {
        var client = context.CreateClient();
        var deviceClaim = includeDeviceId ? $", DeviceId=\"{deviceId}\"" : string.Empty;
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            $"MediaBrowser Client=\"{clientName}\"{deviceClaim}, Device=\"Apple%20II\", Version=\"10.8.0\", Token={accessToken}");
        return client;
    }
}
