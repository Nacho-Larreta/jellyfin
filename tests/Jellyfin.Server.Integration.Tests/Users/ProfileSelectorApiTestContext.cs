using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Api.Models.ProfileSelectorsDtos;
using Jellyfin.Api.Models.UserDtos;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Dto;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Users;

internal sealed class ProfileSelectorApiTestContext
{
    private readonly JellyfinApplicationFactory _factory;

    private ProfileSelectorApiTestContext(JellyfinApplicationFactory factory, HttpClient ownerClient, Guid ownerId, string ownerName, Guid profileId)
    {
        _factory = factory;
        OwnerClient = ownerClient;
        OwnerId = ownerId;
        OwnerName = ownerName;
        ProfileId = profileId;
    }

    public JsonSerializerOptions JsonOptions { get; } = JsonDefaults.Options;

    public HttpClient OwnerClient { get; }

    public Guid OwnerId { get; }

    public string OwnerName { get; }

    public Guid ProfileId { get; }

    public string ActivationUrl => $"ProfileSelectors/Current/Profiles/{ProfileId.ToString("N", CultureInfo.InvariantCulture)}/Activate";

    public string PinUrl => $"Users/{OwnerId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector/Profiles/{ProfileId.ToString("N", CultureInfo.InvariantCulture)}/Pin";

    public static async Task<ProfileSelectorApiTestContext> CreateAsync(JellyfinApplicationFactory factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(await AuthHelper.CompleteStartupAsync(client));
        var owner = await AuthHelper.GetUserDtoAsync(client);

        using var createUserResponse = await client.PostAsJsonAsync(
            "Users/New",
            new CreateUserByName { Name = "selectorBoundaryProfile" },
            JsonDefaults.Options,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, createUserResponse.StatusCode);
        var profile = await createUserResponse.Content.ReadFromJsonAsync<UserDto>(JsonDefaults.Options, TestContext.Current.CancellationToken);
        Assert.NotNull(profile);

        var context = new ProfileSelectorApiTestContext(factory, client, owner.Id, owner.Name, profile.Id);
        await context.ConfigureAsync(isEnabled: true, isVisible: true, includeProfile: true);
        return context;
    }

    public async Task ConfigureAsync(bool isEnabled, bool isVisible, bool includeProfile)
    {
        var request = new ProfileSelectorUpdateRequestDto
        {
            IsEnabled = isEnabled,
            AutoSelectSingleProfile = false,
            Profiles =
            {
                new ProfileSelectorMemberUpdateRequestDto
                {
                    ProfileUserId = OwnerId,
                    DisplayOrder = 0,
                    IsVisible = true
                }
            }
        };
        if (includeProfile)
        {
            request.Profiles.Add(new ProfileSelectorMemberUpdateRequestDto
            {
                ProfileUserId = ProfileId,
                DisplayOrder = 1,
                IsVisible = isVisible
            });
        }

        using var response = await OwnerClient.PutAsJsonAsync(
            $"Users/{OwnerId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector",
            request,
            JsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async Task<UserDto> GetProfileAsync()
    {
        using var response = await OwnerClient.GetAsync(
            $"Users/{ProfileId.ToString("N", CultureInfo.InvariantCulture)}",
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<UserDto>(JsonOptions, TestContext.Current.CancellationToken);
        return Assert.IsType<UserDto>(profile);
    }

    public HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(accessToken);
        return client;
    }

    public HttpClient CreateClient()
        => _factory.CreateClient();

    public void ReplaceOwnerToken(string accessToken)
    {
        OwnerClient.DefaultRequestHeaders.Remove(AuthHelper.AuthHeaderName);
        OwnerClient.DefaultRequestHeaders.AddAuthHeader(accessToken);
    }
}
