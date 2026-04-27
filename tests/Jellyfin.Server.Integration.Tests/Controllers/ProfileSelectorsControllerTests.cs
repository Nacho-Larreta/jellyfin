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
using Xunit;
using Xunit.v3.Priority;

namespace Jellyfin.Server.Integration.Tests.Controllers
{
    [TestCaseOrderer(typeof(PriorityOrderer))]
    public sealed class ProfileSelectorsControllerTests : IClassFixture<JellyfinApplicationFactory>
    {
        private const string ChildUsername = "selectorChild01";

        private readonly JellyfinApplicationFactory _factory;
        private readonly JsonSerializerOptions _jsonOptions = JsonDefaults.Options;
        private static string? _adminAccessToken;
        private static string? _childAccessToken;
        private static Guid _adminUserId = Guid.Empty;
        private static Guid _childUserId = Guid.Empty;

        public ProfileSelectorsControllerTests(JellyfinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        [Priority(-1)]
        public async Task ConfigureSelector_Valid_Success()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_adminAccessToken ??= await AuthHelper.CompleteStartupAsync(client));

            var adminUser = await AuthHelper.GetUserDtoAsync(client);
            _adminUserId = adminUser.Id;

            using var createUserResponse = await client.PostAsJsonAsync(
                "Users/New",
                new CreateUserByName
                {
                    Name = ChildUsername
                },
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, createUserResponse.StatusCode);

            var createdUser = await createUserResponse.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(createdUser);
            _childUserId = createdUser!.Id;

            using var configureSelectorResponse = await client.PutAsJsonAsync(
                $"Users/{_adminUserId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector",
                new ProfileSelectorUpdateRequestDto
                {
                    IsEnabled = true,
                    AutoSelectSingleProfile = false,
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
                            ProfileUserId = _childUserId,
                            DisplayOrder = 1,
                            IsVisible = true
                        }
                    }
                },
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, configureSelectorResponse.StatusCode);

            var selector = await configureSelectorResponse.Content.ReadFromJsonAsync<ProfileSelectorDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(selector);
            Assert.True(selector!.IsEnabled);
            Assert.Equal(_adminUserId, selector.OwnerUserId);
            Assert.Equal(2, selector.Profiles.Count);
        }

        [Fact]
        [Priority(0)]
        public async Task GetCurrentSelector_WithOwnerToken_ReturnsProfiles()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_adminAccessToken!);

            using var response = await client.GetAsync("ProfileSelectors/Current", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var selector = await response.Content.ReadFromJsonAsync<ProfileSelectorDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(selector);
            Assert.Equal(_adminUserId, selector!.OwnerUserId);
            Assert.Null(selector.CurrentDeviceProfileUserId);
            Assert.Contains(selector.Profiles, profile => profile.ProfileUserId.Equals(_childUserId));
        }

        [Fact]
        [Priority(1)]
        public async Task ActivateProfile_WithoutPin_ReturnsRuntimeToken()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_adminAccessToken!);

            using var response = await client.PostAsJsonAsync(
                $"ProfileSelectors/Current/Profiles/{_childUserId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
                new ProfileActivationRequestDto(),
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var activation = await response.Content.ReadFromJsonAsync<ProfileActivationResult>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(activation);
            Assert.Equal(_childUserId, activation!.ActiveProfileUserId);
            Assert.NotNull(activation.AuthenticationResult);
            Assert.Equal(_childUserId, activation.AuthenticationResult!.User.Id);
            Assert.False(string.IsNullOrWhiteSpace(activation.AuthenticationResult.AccessToken));

            _childAccessToken = activation.AuthenticationResult.AccessToken;
        }

        [Fact]
        [Priority(2)]
        public async Task GetCurrentSelector_WithActiveProfileToken_RestoresLastProfile()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_childAccessToken!);

            using var selectorResponse = await client.GetAsync("ProfileSelectors/Current", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, selectorResponse.StatusCode);

            var selector = await selectorResponse.Content.ReadFromJsonAsync<ProfileSelectorDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(selector);
            Assert.Equal(_childUserId, selector!.CurrentDeviceProfileUserId);
            Assert.Contains(selector.Profiles, profile => profile.ProfileUserId.Equals(_childUserId) && profile.IsActive);

            using var meResponse = await client.GetAsync("Users/Me", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, meResponse.StatusCode);

            var currentUser = await meResponse.Content.ReadFromJsonAsync<UserDto>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(currentUser);
            Assert.Equal(_childUserId, currentUser!.Id);
        }

        [Fact]
        [Priority(3)]
        public async Task ActivatePinnedProfile_RequiresPinAndRejectsInvalidPin()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_adminAccessToken!);

            using var setPinResponse = await client.PostAsJsonAsync(
                $"Users/{_adminUserId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector/Profiles/{_childUserId.ToString("N", CultureInfo.InvariantCulture)}/Pin",
                new ProfilePinUpdateRequestDto
                {
                    Pin = "1234"
                },
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, setPinResponse.StatusCode);

            using var missingPinResponse = await client.PostAsJsonAsync(
                $"ProfileSelectors/Current/Profiles/{_childUserId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
                new ProfileActivationRequestDto(),
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Conflict, missingPinResponse.StatusCode);

            using var invalidPinResponse = await client.PostAsJsonAsync(
                $"ProfileSelectors/Current/Profiles/{_childUserId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
                new ProfileActivationRequestDto
                {
                    Pin = "0000"
                },
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, invalidPinResponse.StatusCode);

            using var validPinResponse = await client.PostAsJsonAsync(
                $"ProfileSelectors/Current/Profiles/{_childUserId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
                new ProfileActivationRequestDto
                {
                    Pin = "1234"
                },
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, validPinResponse.StatusCode);
        }

        [Fact]
        [Priority(4)]
        public async Task ClearPinnedProfile_WithCurrentPin_ValidatesBeforeClearing()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_adminAccessToken!);
            var clearPinUrl = $"Users/{_adminUserId.ToString("N", CultureInfo.InvariantCulture)}/ProfileSelector/Profiles/{_childUserId.ToString("N", CultureInfo.InvariantCulture)}/Pin";

            using var invalidClearPinRequest = new HttpRequestMessage(HttpMethod.Delete, clearPinUrl)
            {
                Content = JsonContent.Create(
                    new ProfilePinClearRequestDto
                    {
                        Pin = "0000"
                    },
                    options: _jsonOptions)
            };
            using var invalidClearPinResponse = await client.SendAsync(invalidClearPinRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Forbidden, invalidClearPinResponse.StatusCode);

            using var validClearPinRequest = new HttpRequestMessage(HttpMethod.Delete, clearPinUrl)
            {
                Content = JsonContent.Create(
                    new ProfilePinClearRequestDto
                    {
                        Pin = "1234"
                    },
                    options: _jsonOptions)
            };
            using var validClearPinResponse = await client.SendAsync(validClearPinRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, validClearPinResponse.StatusCode);

            using var activationWithoutPinResponse = await client.PostAsJsonAsync(
                $"ProfileSelectors/Current/Profiles/{_childUserId.ToString("N", CultureInfo.InvariantCulture)}/Activate",
                new ProfileActivationRequestDto(),
                _jsonOptions,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, activationWithoutPinResponse.StatusCode);
        }

        [Fact]
        [Priority(5)]
        public async Task GetSecondaryProfileUserIds_ReturnsOnlyBackingProfileUsers()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_adminAccessToken!);

            using var response = await client.GetAsync("ProfileSelectors/SecondaryProfileUserIds", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var userIds = await response.Content.ReadFromJsonAsync<Guid[]>(_jsonOptions, TestContext.Current.CancellationToken);
            Assert.NotNull(userIds);
            Assert.Contains(_childUserId, userIds!);
            Assert.DoesNotContain(_adminUserId, userIds!);
        }
    }
}
