using System;
using System.Threading.Tasks;
using Jellyfin.Data.Dtos;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Server.Implementations.Tests.Logging;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.SessionManager;

public class SessionManagerTests
{
    [Fact]
    public async Task CreateProfileSwitchCredential_DoesNotChangeActiveSessionBeforeCommit()
    {
        const string DeviceId = "profile-switch-device";
        var owner = new User("owner", "default", "default") { LastActivityDate = DateTime.UtcNow };
        var target = new User("target", "default", "default") { LastActivityDate = DateTime.UtcNow };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(owner.Id)).Returns(owner);
        userManager.Setup(manager => manager.GetUserById(target.Id)).Returns(target);
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(manager => manager.CanAccessDevice(target, DeviceId)).Returns(true);
        deviceManager.Setup(manager => manager.GetDeviceOptions(DeviceId)).Returns((DeviceOptionsDto?)null);
        await using var sessionManager = CreateSessionManager(userManager.Object, deviceManager.Object);
        var oldSession = await sessionManager.LogSessionActivity(
            "app",
            "1",
            DeviceId,
            "device",
            "127.0.0.1",
            owner);
        var switchId = Guid.NewGuid();

        await using var reservation = await sessionManager.CreateProfileSwitchCredential(
            new AuthenticationRequest
            {
                UserId = target.Id,
                App = "app",
                AppVersion = "1",
                DeviceId = DeviceId,
                DeviceName = "device",
                RemoteEndPoint = "127.0.0.1"
            },
            switchId);
        var credential = reservation.Credential;

        Assert.Equal(target.Id, credential.UserId);
        Assert.Equal(switchId, credential.ProfileSwitchId);
        Assert.Equal(0, credential.Id);
        Assert.Equal(owner.Id, oldSession.UserId);
        Assert.Same(oldSession, Assert.Single(sessionManager.Sessions));
        deviceManager.Verify(manager => manager.CreateDevice(It.IsAny<Device>()), Times.Never);
    }

    [Fact]
    public async Task CreateProfileSwitchCredential_DoesNotCountSameDeviceRecoverySessionAgainstLimit()
    {
        const string DeviceId = "profile-switch-device";
        var target = new User("target", "default", "default")
        {
            LastActivityDate = DateTime.UtcNow,
            MaxActiveSessions = 1
        };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(target.Id)).Returns(target);
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(manager => manager.CanAccessDevice(target, DeviceId)).Returns(true);
        deviceManager.Setup(manager => manager.GetDeviceOptions(DeviceId)).Returns((DeviceOptionsDto?)null);
        await using var sessionManager = CreateSessionManager(userManager.Object, deviceManager.Object);
        await sessionManager.LogSessionActivity("app", "1", DeviceId, "device", "127.0.0.1", target);

        await using var reservation = await sessionManager.CreateProfileSwitchCredential(
            new AuthenticationRequest
            {
                UserId = target.Id,
                App = "app",
                AppVersion = "1",
                DeviceId = DeviceId,
                DeviceName = "device",
                RemoteEndPoint = "127.0.0.1"
            },
            Guid.NewGuid());
        var credential = reservation.Credential;

        Assert.Equal(target.Id, credential.UserId);
        deviceManager.Verify(manager => manager.CreateDevice(It.IsAny<Device>()), Times.Never);
    }

    [Fact]
    public async Task StopProfileSwitchPlayback_WhenPlaybackAdvancedFromAToB_PreservesBAndRejectsStaleA()
    {
        const string DeviceId = "profile-switch-device";
        const string Client = "profile-switch-client";
        const string PlaySessionA = "play-session-a";
        const string PlaySessionB = "play-session-b";
        var user = new User("owner", "default", "default") { LastActivityDate = DateTime.UtcNow };
        var userManager = new Mock<IUserManager>();
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(manager => manager.GetDeviceOptions(DeviceId)).Returns((DeviceOptionsDto?)null);
        await using var sessionManager = CreateSessionManager(userManager.Object, deviceManager.Object);
        var session = await sessionManager.LogSessionActivity(
            Client,
            "1",
            DeviceId,
            "device",
            "127.0.0.1",
            user);
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        session.NowPlayingItem = new BaseItemDto { Id = itemA };
        session.PlaySessionId = PlaySessionA;
        var staleStopRequest = new ProfileSwitchSessionStopRequest
        {
            UserId = user.Id,
            DeviceId = DeviceId,
            Client = Client,
            ItemId = itemA,
            PlaySessionId = PlaySessionA,
            PositionTicks = 1
        };

        var replacementPlayback = new BaseItemDto { Id = itemB };
        session.NowPlayingItem = replacementPlayback;
        session.PlaySessionId = PlaySessionB;
        var result = await sessionManager.StopProfileSwitchPlaybackAsync(staleStopRequest);

        Assert.Equal(ProfileSwitchSessionStopOutcome.PlaybackMismatch, result.Outcome);
        Assert.Same(replacementPlayback, session.NowPlayingItem);
        Assert.Equal(PlaySessionB, session.PlaySessionId);
    }

    [Fact]
    public async Task ProfileSwitchAdmission_WhenConcurrentLoginUsesAnotherDevice_AllowsExactlyOneSession()
    {
        const string SwitchDeviceId = "profile-switch-device";
        const string LoginDeviceId = "login-device";
        var target = new User("target", "default", "default")
        {
            LastActivityDate = DateTime.UtcNow,
            MaxActiveSessions = 1
        };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(target.Id)).Returns(target);
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(manager => manager.CanAccessDevice(target, It.IsAny<string>())).Returns(true);
        deviceManager.Setup(manager => manager.GetDeviceOptions(It.IsAny<string>())).Returns((DeviceOptionsDto?)null);
        await using var sessionManager = CreateSessionManager(userManager.Object, deviceManager.Object);
        var reservation = await sessionManager.CreateProfileSwitchCredential(
            CreateAuthenticationRequest(target.Id, SwitchDeviceId),
            Guid.NewGuid());
        var competingLogin = sessionManager.AuthenticateDirect(
            CreateAuthenticationRequest(target.Id, LoginDeviceId));

        Assert.False(competingLogin.IsCompleted);
        try
        {
            await sessionManager.LogSessionActivity(
                "app",
                "1",
                SwitchDeviceId,
                "device",
                "127.0.0.1",
                target);
            reservation.RevalidateSessionPolicy(target);
        }
        finally
        {
            await reservation.DisposeAsync();
        }

        await Assert.ThrowsAsync<SecurityException>(() => competingLogin);
        var activeSession = Assert.Single(sessionManager.Sessions);
        Assert.Equal(SwitchDeviceId, activeSession.DeviceId);
        Assert.Equal(target.Id, activeSession.UserId);
    }

    [Fact]
    public async Task AuthenticateDirect_WhenReplacingSameDeviceAtLimit_IsAllowed()
    {
        const string DeviceId = "same-device";
        var target = new User("target", "default", "default")
        {
            LastActivityDate = DateTime.UtcNow,
            MaxActiveSessions = 1
        };
        var userManager = new Mock<IUserManager>();
        userManager.Setup(manager => manager.GetUserById(target.Id)).Returns(target);
        userManager
            .Setup(manager => manager.GetUserDto(target, It.IsAny<string>()))
            .Returns(new UserDto { Id = target.Id, Name = target.Username });
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(manager => manager.CanAccessDevice(target, DeviceId)).Returns(true);
        deviceManager.Setup(manager => manager.GetDeviceOptions(DeviceId)).Returns((DeviceOptionsDto?)null);
        deviceManager
            .Setup(manager => manager.GetDevices(It.IsAny<DeviceQuery>()))
            .Returns(new QueryResult<Device>(null, 0, []));
        deviceManager
            .Setup(manager => manager.CreateDevice(It.IsAny<Device>()))
            .Returns((Device device) => Task.FromResult(device));
        await using var sessionManager = CreateSessionManager(userManager.Object, deviceManager.Object);
        var existingSession = await sessionManager.LogSessionActivity(
            "app",
            "1",
            DeviceId,
            "device",
            "127.0.0.1",
            target);

        var result = await sessionManager.AuthenticateDirect(CreateAuthenticationRequest(target.Id, DeviceId));

        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.Same(existingSession, Assert.Single(sessionManager.Sessions));
    }

    [Fact]
    public async Task RevokeProfileSwitchCredential_PreservesSessionWhenRecoveryCredentialRemains()
    {
        const string DeviceId = "profile-switch-device";
        var owner = new User("owner", "default", "default") { LastActivityDate = DateTime.UtcNow };
        var recoveryCredential = new Device(owner.Id, "app", "1", "device", DeviceId);
        var switchCredential = new Device(owner.Id, "app", "1", "device", DeviceId)
        {
            ProfileSwitchId = Guid.NewGuid()
        };
        var userManager = new Mock<IUserManager>();
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager.Setup(manager => manager.GetDeviceOptions(DeviceId)).Returns((DeviceOptionsDto?)null);
        deviceManager.Setup(manager => manager.DeleteDevice(switchCredential)).Returns(Task.CompletedTask);
        deviceManager
            .Setup(manager => manager.GetDevices(It.IsAny<DeviceQuery>()))
            .Returns(new QueryResult<Device>(null, 1, [recoveryCredential]));
        await using var sessionManager = CreateSessionManager(userManager.Object, deviceManager.Object);
        var recoverySession = await sessionManager.LogSessionActivity(
            "app",
            "1",
            DeviceId,
            "device",
            "127.0.0.1",
            owner);

        await sessionManager.RevokeProfileSwitchCredential(switchCredential);

        Assert.Same(recoverySession, Assert.Single(sessionManager.Sessions));
    }

    [Fact]
    public async Task Logout_LogsDeviceAndUserWithoutAccessToken()
    {
        const string AccessTokenMarker = "LOGOUT_ACCESS_TOKEN_MARKER";
        var logger = new CapturingLogger<Emby.Server.Implementations.Session.SessionManager>();
        var deviceManager = new Mock<IDeviceManager>();
        deviceManager
            .Setup(manager => manager.DeleteDevice(It.IsAny<Device>()))
            .Returns(Task.CompletedTask);
        await using var sessionManager = new Emby.Server.Implementations.Session.SessionManager(
            logger,
            Mock.Of<IEventManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            Mock.Of<IMusicManager>(),
            Mock.Of<IDtoService>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IServerApplicationHost>(),
            deviceManager.Object,
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IHostApplicationLifetime>());
        var userId = Guid.NewGuid();
        var device = new Device(userId, "app", "1", "device", "device-id")
        {
            AccessToken = AccessTokenMarker
        };

        await sessionManager.Logout(device);

        var message = Assert.Single(logger.Messages);
        Assert.Contains("device-id", message, StringComparison.Ordinal);
        Assert.Contains(userId.ToString(), message, StringComparison.Ordinal);
        Assert.DoesNotContain(AccessTokenMarker, message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", typeof(ArgumentException))]
    [InlineData(null, typeof(ArgumentNullException))]
    public async Task GetAuthorizationToken_Should_ThrowException(string? deviceId, Type exceptionType)
    {
        await using var sessionManager = new Emby.Server.Implementations.Session.SessionManager(
            NullLogger<Emby.Server.Implementations.Session.SessionManager>.Instance,
            Mock.Of<IEventManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            Mock.Of<IMusicManager>(),
            Mock.Of<IDtoService>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IServerApplicationHost>(),
            Mock.Of<IDeviceManager>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IHostApplicationLifetime>());

        await Assert.ThrowsAsync(exceptionType, () => sessionManager.GetAuthorizationToken(
            new User("test", "default", "default"),
            deviceId,
            "app_name",
            "0.0.0",
            "device_name"));
    }

    [Theory]
    [MemberData(nameof(AuthenticateNewSessionInternal_Exception_TestData))]
    public async Task AuthenticateNewSessionInternal_Should_ThrowException(AuthenticationRequest authenticationRequest, Type exceptionType)
    {
        await using var sessionManager = new Emby.Server.Implementations.Session.SessionManager(
            NullLogger<Emby.Server.Implementations.Session.SessionManager>.Instance,
            Mock.Of<IEventManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<ILibraryManager>(),
            Mock.Of<IUserManager>(),
            Mock.Of<IMusicManager>(),
            Mock.Of<IDtoService>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IServerApplicationHost>(),
            Mock.Of<IDeviceManager>(),
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IHostApplicationLifetime>());

        await Assert.ThrowsAsync(exceptionType, () => sessionManager.AuthenticateNewSessionInternal(authenticationRequest, false));
    }

    public static TheoryData<AuthenticationRequest, Type> AuthenticateNewSessionInternal_Exception_TestData()
    {
        var data = new TheoryData<AuthenticationRequest, Type>
        {
            {
                new AuthenticationRequest { App = string.Empty, DeviceId = "device_id", DeviceName = "device_name", AppVersion = "app_version" },
                typeof(ArgumentException)
            },
            {
                new AuthenticationRequest { App = null, DeviceId = "device_id", DeviceName = "device_name", AppVersion = "app_version" },
                typeof(ArgumentNullException)
            },
            {
                new AuthenticationRequest { App = "app_name", DeviceId = string.Empty, DeviceName = "device_name", AppVersion = "app_version" },
                typeof(ArgumentException)
            },
            {
                new AuthenticationRequest { App = "app_name", DeviceId = null, DeviceName = "device_name", AppVersion = "app_version" },
                typeof(ArgumentNullException)
            },
            {
                new AuthenticationRequest { App = "app_name", DeviceId = "device_id", DeviceName = string.Empty, AppVersion = "app_version" },
                typeof(ArgumentException)
            },
            {
                new AuthenticationRequest { App = "app_name", DeviceId = "device_id", DeviceName = null, AppVersion = "app_version" },
                typeof(ArgumentNullException)
            },
            {
                new AuthenticationRequest { App = "app_name", DeviceId = "device_id", DeviceName = "device_name", AppVersion = string.Empty },
                typeof(ArgumentException)
            },
            {
                new AuthenticationRequest { App = "app_name", DeviceId = "device_id", DeviceName = "device_name", AppVersion = null },
                typeof(ArgumentNullException)
            }
        };

        return data;
    }

    private static AuthenticationRequest CreateAuthenticationRequest(Guid userId, string deviceId)
        => new()
        {
            UserId = userId,
            App = "app",
            AppVersion = "1",
            DeviceId = deviceId,
            DeviceName = "device",
            RemoteEndPoint = "127.0.0.1"
        };

    private static Emby.Server.Implementations.Session.SessionManager CreateSessionManager(
        IUserManager userManager,
        IDeviceManager deviceManager)
        => new(
            NullLogger<Emby.Server.Implementations.Session.SessionManager>.Instance,
            Mock.Of<IEventManager>(),
            Mock.Of<IUserDataManager>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<ILibraryManager>(),
            userManager,
            Mock.Of<IMusicManager>(),
            Mock.Of<IDtoService>(),
            Mock.Of<IImageProcessor>(),
            Mock.Of<IServerApplicationHost>(),
            deviceManager,
            Mock.Of<IMediaSourceManager>(),
            Mock.Of<IHostApplicationLifetime>());
}
