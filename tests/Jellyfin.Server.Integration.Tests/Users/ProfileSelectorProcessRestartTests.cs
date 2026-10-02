using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.ProfileSelectorsDtos;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.System;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Users;

public sealed class ProfileSelectorProcessRestartTests
{
    private const string DeviceA = "selector-process-device-a";
    private const string DeviceB = "selector-process-device-b";
    private const string ProtectedPin = "01234567";

    [Fact]
    public async Task TwoRememberedDevicesRemainIndependentAcrossServerProcessRestart()
    {
        await using var server = new ProfileSelectorProcessHost();
        var firstProcessId = await server.StartAsync(TestContext.Current.CancellationToken);
        using var bootstrapClient = server.CreateClient();
        var bootstrapToken = await AuthHelper.CompleteStartupAsync(bootstrapClient);
        bootstrapClient.DefaultRequestHeaders.AddAuthHeader(bootstrapToken);
        var owner = await AuthHelper.GetUserDtoAsync(bootstrapClient);
        var unprotected = await CreateProfileAsync(bootstrapClient, "restartAdultA");
        var protectedProfile = await CreateProfileAsync(bootstrapClient, "restartAdultB");
        await ConfigureSelectorAsync(bootstrapClient, owner.Id, unprotected.Id, protectedProfile.Id);
        await SetPinAsync(bootstrapClient, owner.Id, protectedProfile.Id);

        var ownerTokenA = await AuthenticateAsync(server, owner.Name, DeviceA);
        var ownerTokenB = await AuthenticateAsync(server, owner.Name, DeviceB);
        var activeTokenA = await ActivateAsync(server, ownerTokenA, DeviceA, unprotected.Id, null);
        var activeTokenB = await ActivateAsync(server, ownerTokenB, DeviceB, protectedProfile.Id, ProtectedPin);

        await AssertRememberedProfileAsync(server, ownerTokenA, DeviceA, unprotected.Id, requiresPin: false);
        await AssertRememberedProfileAsync(server, activeTokenA, DeviceA, unprotected.Id, requiresPin: false);
        await AssertRememberedProfileAsync(server, ownerTokenB, DeviceB, protectedProfile.Id, requiresPin: true);
        await AssertRememberedProfileAsync(server, activeTokenB, DeviceB, protectedProfile.Id, requiresPin: true);

        using var infoResponse = await bootstrapClient.GetAsync("System/Info/Public", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, infoResponse.StatusCode);
        var info = await infoResponse.Content.ReadFromJsonAsync<PublicSystemInfo>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(info?.Id));
        Assert.True(info.StartupWizardCompleted);

        await server.StopAsync(TestContext.Current.CancellationToken);
        var secondProcessId = await server.StartAsync(TestContext.Current.CancellationToken, info.Id);
        Assert.NotEqual(firstProcessId, secondProcessId);

        await AssertRememberedProfileAsync(server, ownerTokenA, DeviceA, unprotected.Id, requiresPin: false);
        await AssertRememberedProfileAsync(server, activeTokenA, DeviceA, unprotected.Id, requiresPin: false);
        await AssertRememberedProfileAsync(server, ownerTokenB, DeviceB, protectedProfile.Id, requiresPin: true);
        await AssertRememberedProfileAsync(server, activeTokenB, DeviceB, protectedProfile.Id, requiresPin: true);
    }

    private static async Task<UserDto> CreateProfileAsync(HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync(
            "Users/New",
            new CreateUserByName { Name = name },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        return Assert.IsType<UserDto>(profile);
    }

    private static async Task ConfigureSelectorAsync(HttpClient client, Guid ownerId, Guid unprotectedId, Guid protectedId)
    {
        using var response = await client.PutAsJsonAsync(
            $"Users/{ownerId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector",
            new ProfileSelectorUpdateRequestDto
            {
                IsEnabled = true,
                Profiles =
                {
                    new ProfileSelectorMemberUpdateRequestDto { ProfileUserId = ownerId, DisplayOrder = 0, IsVisible = true },
                    new ProfileSelectorMemberUpdateRequestDto { ProfileUserId = unprotectedId, DisplayOrder = 1, IsVisible = true },
                    new ProfileSelectorMemberUpdateRequestDto { ProfileUserId = protectedId, DisplayOrder = 2, IsVisible = true }
                }
            },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task SetPinAsync(HttpClient client, Guid ownerId, Guid profileId)
    {
        using var response = await client.PostAsJsonAsync(
            $"Users/{ownerId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector/Profiles/{profileId.ToString("N", CultureInfo.InvariantCulture)}/Pin",
            new ProfilePinUpdateRequestDto { Pin = ProtectedPin },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> AuthenticateAsync(ProfileSelectorProcessHost server, string username, string deviceId)
    {
        using var client = server.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, CreateAuthorizationHeader(deviceId));
        request.Content = JsonContent.Create(
            new AuthenticateUserByName { Username = username, Pw = string.Empty },
            options: JsonDefaults.Options);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authentication = await response.Content.ReadFromJsonAsync<AuthenticationResult>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(authentication?.AccessToken));
        return authentication!.AccessToken;
    }

    private static async Task<string> ActivateAsync(ProfileSelectorProcessHost server, string token, string deviceId, Guid profileId, string? pin)
    {
        using var client = CreateAuthenticatedClient(server, token, deviceId);
        using var response = await client.PostAsJsonAsync(
            $"ProfileSelectors/Current/Profiles/{profileId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
            new ProfileActivationRequestDto { Pin = pin },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activation = await response.Content.ReadFromJsonAsync<ProfileActivationResult>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(profileId, activation?.ActiveProfileUserId);
        Assert.False(string.IsNullOrWhiteSpace(activation?.AuthenticationResult?.AccessToken));
        return activation!.AuthenticationResult!.AccessToken;
    }

    private static async Task AssertRememberedProfileAsync(ProfileSelectorProcessHost server, string token, string deviceId, Guid expectedProfileId, bool requiresPin)
    {
        using var client = CreateAuthenticatedClient(server, token, deviceId);
        using var response = await client.GetAsync("ProfileSelectors/Current", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var selector = await response.Content.ReadFromJsonAsync<ProfileSelectorDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.Equal(expectedProfileId, selector?.CurrentDeviceProfileUserId);
        Assert.Equal(requiresPin, selector!.CurrentDeviceProfileRequiresPin);
    }

    private static HttpClient CreateAuthenticatedClient(ProfileSelectorProcessHost server, string token, string deviceId)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            $"{CreateAuthorizationHeader(deviceId)}, Token={token}");
        return client;
    }

    private static string CreateAuthorizationHeader(string deviceId)
        => $"MediaBrowser Client=\"Jellyfin.Server%20Integration%20Tests\", DeviceId=\"{deviceId}\", Device=\"Apple%20II\", Version=\"10.8.0\"";
}
