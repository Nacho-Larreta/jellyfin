using System;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Server.Integration.Tests
{
    public sealed class WebSocketTests : IClassFixture<JellyfinApplicationFactory>
    {
        private readonly JellyfinApplicationFactory _factory;

        public WebSocketTests(JellyfinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task WebSocket_ApiKeyQueryParameters_UseOnlyCanonicalCasingWhenLegacyAuthorizationIsDisabled()
        {
            var server = _factory.Server;
            var configurationManager = _factory.Services.GetRequiredService<IServerConfigurationManager>();
            Assert.False(configurationManager.Configuration.EnableLegacyAuthorization);

            using var authenticationClient = _factory.CreateClient();
            var accessToken = await AuthHelper.CompleteStartupAsync(authenticationClient);
            var canonicalClient = server.CreateWebSocketClient();

            using var canonicalSocket = await canonicalClient.ConnectAsync(
                CreateWebSocketUri(server.BaseAddress, "ApiKey", accessToken),
                CancellationToken.None);
            Assert.Equal(WebSocketState.Open, canonicalSocket.State);

            var legacyClient = server.CreateWebSocketClient();
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => legacyClient.ConnectAsync(CreateWebSocketUri(server.BaseAddress, "api_key", accessToken), CancellationToken.None));

            await canonicalSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }

        [Fact]
        public async Task WebSocket_Unauthenticated_ThrowsInvalidOperationException()
        {
            var server = _factory.Server;
            var client = server.CreateWebSocketClient();

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => client.ConnectAsync(
                    new UriBuilder(server.BaseAddress)
                    {
                        Scheme = "ws",
                        Path = "websocket"
                    }.Uri,
                    CancellationToken.None));
        }

        private static Uri CreateWebSocketUri(Uri baseAddress, string queryParameterName, string accessToken)
        {
            return new UriBuilder(baseAddress)
            {
                Scheme = "ws",
                Path = "websocket",
                Query = $"{queryParameterName}={Uri.EscapeDataString(accessToken)}"
            }.Uri;
        }
    }
}
