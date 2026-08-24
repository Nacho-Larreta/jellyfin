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
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Explore;
using Xunit;
using Xunit.v3.Priority;

namespace Jellyfin.Server.Integration.Tests.Controllers;

[TestCaseOrderer(typeof(PriorityOrderer))]
public sealed class SearchHistoryControllerTests : IClassFixture<JellyfinApplicationFactory>
{
    private readonly JellyfinApplicationFactory _factory;
    private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
    private static string? _adminAccessToken;
    private static string? _profileAccessToken;
    private static Guid _adminUserId;
    private static Guid _profileUserId;
    private static Guid _unlinkedUserId;

    public SearchHistoryControllerTests(JellyfinApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    [Priority(-1)]
    public async Task ConfigureProfileSelectorFixture()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(_adminAccessToken ??= await AuthHelper.CompleteStartupAsync(client));
        _adminUserId = (await AuthHelper.GetUserDtoAsync(client)).Id;

        _profileUserId = await CreateUserAsync(client, "historyProfile01");
        _unlinkedUserId = await CreateUserAsync(client, "historyUnlinked01");

        using var configureSelectorResponse = await client.PutAsJsonAsync(
            $"Users/{_adminUserId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector",
            new ProfileSelectorUpdateRequestDto
            {
                IsEnabled = true,
                Profiles =
                {
                    new ProfileSelectorMemberUpdateRequestDto
                    {
                        ProfileUserId = _adminUserId,
                        DisplayOrder = 0,
                        IsVisible = true
                    },
                    new ProfileSelectorMemberUpdateRequestDto
                    {
                        ProfileUserId = _profileUserId,
                        DisplayOrder = 1,
                        IsVisible = true
                    }
                }
            },
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, configureSelectorResponse.StatusCode);

        using var activationResponse = await client.PostAsJsonAsync(
            $"ProfileSelectors/Current/Profiles/{_profileUserId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
            new ProfileActivationRequestDto(),
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, activationResponse.StatusCode);
        var activation = await activationResponse.Content.ReadFromJsonAsync<ProfileActivationResult>(_jsonOptions, TestContext.Current.CancellationToken);
        _profileAccessToken = activation!.AuthenticationResult!.AccessToken;
    }

    [Fact]
    [Priority(0)]
    public async Task EndpointsRequireRealMembershipForProfileAndAdministratorTokens()
    {
        var adminClient = CreateAuthenticatedClient(_adminAccessToken!);
        Assert.Equal(HttpStatusCode.Forbidden, await PostTermAsync(adminClient, HistoryUrl(_adminUserId, _profileUserId), "admin linked"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostTermAsync(adminClient, HistoryUrl(_adminUserId, _unlinkedUserId), "admin unlinked"));

        var profileClient = CreateAuthenticatedClient(_profileAccessToken!);
        Assert.Equal(HttpStatusCode.NoContent, await PostTermAsync(profileClient, HistoryUrl(_adminUserId, _profileUserId), "profile linked"));
        Assert.Equal(HttpStatusCode.Forbidden, await PostTermAsync(profileClient, HistoryUrl(_unlinkedUserId, _profileUserId), "forged owner"));
    }

    [Fact]
    [Priority(1)]
    public async Task ConcurrentEquivalentTerms_CreateOneEntryAndIncrementAtomically()
    {
        var profileClient = CreateAuthenticatedClient(_profileAccessToken!);
        var profileHistoryUrl = HistoryUrl(_adminUserId, _profileUserId);
        using (var resetProfileHistoryResponse = await profileClient.DeleteAsync(profileHistoryUrl, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, resetProfileHistoryResponse.StatusCode);
        }

        var responses = await Task.WhenAll(
            PostTermAsync(profileClient, profileHistoryUrl, "Concurrent-Term"),
            PostTermAsync(profileClient, profileHistoryUrl, "Concurrent Term"));

        Assert.All(responses, status => Assert.Equal(HttpStatusCode.NoContent, status));
        var history = await GetHistoryAsync(profileClient, profileHistoryUrl);
        var entry = Assert.Single(history);
        Assert.Equal(2, entry.HitCount);
    }

    [Fact]
    [Priority(2)]
    public async Task HistoryNormalizesDeduplicatesIsolatesOrdersAndClears()
    {
        var profileClient = CreateAuthenticatedClient(_profileAccessToken!);
        var profileHistoryUrl = HistoryUrl(_adminUserId, _profileUserId);
        using (var resetProfileHistoryResponse = await profileClient.DeleteAsync(profileHistoryUrl, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, resetProfileHistoryResponse.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NoContent, await PostTermAsync(profileClient, profileHistoryUrl, "Spider-Man"));
        Assert.Equal(HttpStatusCode.NoContent, await PostTermAsync(profileClient, profileHistoryUrl, "Spider Man"));
        Assert.Equal(HttpStatusCode.NoContent, await PostTermAsync(profileClient, profileHistoryUrl, "Pokémon"));

        var profileHistory = await GetHistoryAsync(profileClient, profileHistoryUrl);
        Assert.Equal(2, profileHistory.Length);
        Assert.Equal("Pokémon", profileHistory[0].SearchTerm);
        Assert.Contains(profileHistory, entry => entry.SearchTerm == "Spider Man" && entry.HitCount == 2);

        var adminClient = CreateAuthenticatedClient(_adminAccessToken!);
        var ownerHistoryUrl = HistoryUrl(_adminUserId, _adminUserId);
        Assert.Equal(HttpStatusCode.NoContent, await PostTermAsync(adminClient, ownerHistoryUrl, "admin owner"));

        using var clearProfileHistoryResponse = await profileClient.DeleteAsync(profileHistoryUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NoContent, clearProfileHistoryResponse.StatusCode);
        Assert.Empty(await GetHistoryAsync(profileClient, profileHistoryUrl));
        Assert.Contains(await GetHistoryAsync(adminClient, ownerHistoryUrl), entry => entry.SearchTerm == "admin owner");
    }

    [Fact]
    [Priority(3)]
    public async Task HistoryRejectsInvalidBoundariesWithoutTruncation()
    {
        var profileClient = CreateAuthenticatedClient(_profileAccessToken!);
        var profileHistoryUrl = HistoryUrl(_adminUserId, _profileUserId);
        using (var resetProfileHistoryResponse = await profileClient.DeleteAsync(profileHistoryUrl, TestContext.Current.CancellationToken))
        {
            Assert.Equal(HttpStatusCode.NoContent, resetProfileHistoryResponse.StatusCode);
        }

        Assert.Equal(HttpStatusCode.BadRequest, await PostTermAsync(profileClient, profileHistoryUrl, string.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, await PostTermAsync(profileClient, profileHistoryUrl, "---"));
        Assert.Equal(HttpStatusCode.NoContent, await PostTermAsync(profileClient, profileHistoryUrl, "a"));
        Assert.Equal(HttpStatusCode.NoContent, await PostTermAsync(profileClient, profileHistoryUrl, new string('a', 255)));
        Assert.Equal(HttpStatusCode.BadRequest, await PostTermAsync(profileClient, profileHistoryUrl, new string('b', 256)));
        Assert.Equal(HttpStatusCode.BadRequest, await PostTermAsync(profileClient, profileHistoryUrl, new string('c', 10_000)));

        var history = await GetHistoryAsync(profileClient, profileHistoryUrl);
        Assert.Equal(2, history.Length);
        Assert.Contains(history, entry => entry.SearchTerm == "a");
        Assert.Contains(history, entry => entry.SearchTerm.Length == 255);
    }

    private HttpClient CreateAuthenticatedClient(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.AddAuthHeader(accessToken);
        return client;
    }

    private async Task<Guid> CreateUserAsync(HttpClient client, string username)
    {
        using var response = await client.PostAsJsonAsync(
            "Users/New",
            new CreateUserByName { Name = username },
            _jsonOptions,
            TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken))!.Id;
    }

    private async Task<HttpStatusCode> PostTermAsync(HttpClient client, string historyUrl, string searchTerm)
    {
        using var response = await client.PostAsJsonAsync(
            historyUrl,
            new SearchHistoryUpdateRequestDto { SearchTerm = searchTerm },
            _jsonOptions,
            TestContext.Current.CancellationToken);
        return response.StatusCode;
    }

    private async Task<SearchHistoryEntryDto[]> GetHistoryAsync(HttpClient client, string historyUrl)
    {
        using var response = await client.GetAsync(historyUrl, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SearchHistoryEntryDto[]>(_jsonOptions, TestContext.Current.CancellationToken))!;
    }

    private static string HistoryUrl(Guid ownerUserId, Guid profileUserId)
        => $"Users/{ownerUserId.ToString("N", CultureInfo.InvariantCulture)}/Profiles/{profileUserId.ToString("N", CultureInfo.InvariantCulture)}/Search/History";
}
