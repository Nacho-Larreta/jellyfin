using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.ProfileSelectorsDtos;
using Jellyfin.Api.Models.UserDtos;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Users;

public sealed class ProfileSelectorAuthorizationTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;

    public ProfileSelectorAuthorizationTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Activation_AllowsSameSelectorMemberAndRejectsInvalidTargets()
    {
        var context = await ProfileSelectorApiTestContext.CreateAsync(_factory);

        using (var anonymousClient = _factory.CreateClient())
        using (var noAuthenticationResponse = await anonymousClient.PostAsJsonAsync(
                   context.ActivationUrl,
                   new ProfileActivationRequestDto(),
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, noAuthenticationResponse.StatusCode);
        }

        await context.ConfigureAsync(isEnabled: true, isVisible: false, includeProfile: true);
        using (var hiddenResponse = await ActivateAsync(context))
        {
            Assert.Equal(HttpStatusCode.Forbidden, hiddenResponse.StatusCode);
        }

        await context.ConfigureAsync(isEnabled: true, isVisible: true, includeProfile: true);
        var profile = await context.GetProfileAsync();
        profile.Policy.IsDisabled = true;
        await UpdatePolicyAsync(context, profile);
        using (var disabledResponse = await ActivateAsync(context))
        {
            Assert.Equal(HttpStatusCode.Forbidden, disabledResponse.StatusCode);
        }

        profile.Policy.IsDisabled = false;
        await UpdatePolicyAsync(context, profile);

        using var createUnlinkedResponse = await context.OwnerClient.PostAsJsonAsync(
            "Users/New",
            new CreateUserByName { Name = "selectorUnlinkedProfile" },
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, createUnlinkedResponse.StatusCode);
        var unlinkedUser = await createUnlinkedResponse.Content.ReadFromJsonAsync<UserDto>(context.JsonOptions, TestContext.Current.CancellationToken);
        Assert.NotNull(unlinkedUser);

        var unlinkedAccessToken = await AuthenticateAsync(context, unlinkedUser.Name, "unlinked-device");
        using (var unlinkedClient = context.CreateAuthenticatedClient(unlinkedAccessToken))
        using (var unrelatedCallerResponse = await unlinkedClient.PostAsJsonAsync(
                   context.ActivationUrl,
                   new ProfileActivationRequestDto(),
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NotFound, unrelatedCallerResponse.StatusCode);
        }

        using (var unlinkedResponse = await context.OwnerClient.PostAsJsonAsync(
                   $"ProfileSelectors/Current/Profiles/{unlinkedUser.Id.ToString("N", CultureInfo.InvariantCulture)}/Activate",
                   new ProfileActivationRequestDto(),
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.Forbidden, unlinkedResponse.StatusCode);
        }

        using var profileActivationResponse = await ActivateAsync(context);
        Assert.Equal(HttpStatusCode.OK, profileActivationResponse.StatusCode);
        var profileActivation = await profileActivationResponse.Content.ReadFromJsonAsync<ProfileActivationResult>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.NotNull(profileActivation?.AuthenticationResult?.AccessToken);

        using var profileClient = context.CreateAuthenticatedClient(profileActivation!.AuthenticationResult!.AccessToken);
        using (var defaultDeviceSelectorResponse = await profileClient.GetAsync("ProfileSelectors/Current", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, defaultDeviceSelectorResponse.StatusCode);
            var defaultDeviceSelector = await defaultDeviceSelectorResponse.Content.ReadFromJsonAsync<ProfileSelectorDto>(
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(context.ProfileId, defaultDeviceSelector?.CurrentDeviceProfileUserId);
        }

        var secondDeviceOwnerToken = await AuthenticateAsync(context, context.OwnerName, "second-device");
        using (var secondDeviceOwnerClient = CreateAuthenticatedClient(context, secondDeviceOwnerToken, "second-device"))
        using (var secondDeviceSelectorResponse = await secondDeviceOwnerClient.GetAsync("ProfileSelectors/Current", TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, secondDeviceSelectorResponse.StatusCode);
            var secondDeviceSelector = await secondDeviceSelectorResponse.Content.ReadFromJsonAsync<ProfileSelectorDto>(
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Null(secondDeviceSelector?.CurrentDeviceProfileUserId);
        }

        using (var ownerActivationResponse = await profileClient.PostAsJsonAsync(
                   $"ProfileSelectors/Current/Profiles/{context.OwnerId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
                   new ProfileActivationRequestDto(),
                   context.JsonOptions,
                   TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.OK, ownerActivationResponse.StatusCode);
            var ownerActivation = await ownerActivationResponse.Content.ReadFromJsonAsync<ProfileActivationResult>(
                context.JsonOptions,
                TestContext.Current.CancellationToken);
            Assert.NotNull(ownerActivation?.AuthenticationResult?.AccessToken);
            context.ReplaceOwnerToken(ownerActivation!.AuthenticationResult!.AccessToken);
        }

        await context.ConfigureAsync(isEnabled: false, isVisible: true, includeProfile: true);
        using (var selectorDisabledResponse = await ActivateAsync(context))
        {
            Assert.Equal(HttpStatusCode.NotFound, selectorDisabledResponse.StatusCode);
        }

        await context.ConfigureAsync(isEnabled: true, isVisible: true, includeProfile: false);
        using var removedResponse = await ActivateAsync(context);
        Assert.Equal(HttpStatusCode.Forbidden, removedResponse.StatusCode);
    }

    private static Task<HttpResponseMessage> ActivateAsync(ProfileSelectorApiTestContext context)
        => context.OwnerClient.PostAsJsonAsync(
            context.ActivationUrl,
            new ProfileActivationRequestDto(),
            context.JsonOptions,
            TestContext.Current.CancellationToken);

    private static async Task UpdatePolicyAsync(ProfileSelectorApiTestContext context, UserDto profile)
    {
        using var response = await context.OwnerClient.PostAsJsonAsync(
            $"Users/{context.ProfileId.ToString("N", CultureInfo.InvariantCulture)}/Policy",
            profile.Policy,
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private static async Task<string> AuthenticateAsync(ProfileSelectorApiTestContext context, string username, string deviceId)
    {
        using var client = context.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/Users/AuthenticateByName");
        request.Headers.TryAddWithoutValidation(AuthHelper.AuthHeaderName, CreateAuthorizationHeader(deviceId));
        request.Content = JsonContent.Create(
            new AuthenticateUserByName
            {
                Username = username,
                Pw = string.Empty
            },
            options: context.JsonOptions);
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var authentication = await response.Content.ReadFromJsonAsync<AuthenticationResult>(
            context.JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(authentication?.AccessToken));
        return authentication!.AccessToken;
    }

    private static HttpClient CreateAuthenticatedClient(ProfileSelectorApiTestContext context, string accessToken, string deviceId)
    {
        var client = context.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            AuthHelper.AuthHeaderName,
            $"{CreateAuthorizationHeader(deviceId)}, Token={accessToken}");
        return client;
    }

    private static string CreateAuthorizationHeader(string deviceId)
        => $"MediaBrowser Client=\"Jellyfin.Server%20Integration%20Tests\", DeviceId=\"{deviceId}\", Device=\"Apple%20II\", Version=\"10.8.0\"";
}
