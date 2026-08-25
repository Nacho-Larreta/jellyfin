using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Users;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Users;

public sealed class ProfileSelectorManagerTests
{
    private const string DeviceId = "profile-selector-test-device";
    private const string RemoteEndPoint = "203.0.113.10";

    public enum ActivationDenial
    {
        None,
        Hidden,
        Disabled,
        Remote,
        Schedule,
        Device,
        MaxSessions,
        Locked,
        Removed
    }

    [Theory]
    [InlineData(ActivationDenial.Hidden, "PROFILE_NOT_VISIBLE")]
    [InlineData(ActivationDenial.Disabled, "PROFILE_USER_DISABLED")]
    [InlineData(ActivationDenial.Remote, "PROFILE_REMOTE_ACCESS_DENIED")]
    [InlineData(ActivationDenial.Schedule, "PROFILE_ACCESS_SCHEDULE_DENIED")]
    [InlineData(ActivationDenial.Device, "PROFILE_DEVICE_ACCESS_DENIED")]
    [InlineData(ActivationDenial.MaxSessions, "PROFILE_MAX_SESSIONS_REACHED")]
    public async Task ActivateProfileAsync_RejectsEveryMutableEligibilityDenial(ActivationDenial denial, string expectedErrorCode)
    {
        var databasePath = Path.GetTempFileName();
        try
        {
            var testContext = await CreateTestContextAsync(databasePath, denial);

            var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
                () => testContext.Manager.ActivateProfileAsync(CreateActivationContext(testContext.Owner.Id, testContext.Profile.Id), TestContext.Current.CancellationToken));

            Assert.Equal(expectedErrorCode, exception.ErrorCode);
            testContext.SessionManager.Verify(
                manager => manager.AuthenticateDirect(It.IsAny<AuthenticationRequest>()),
                Times.Never);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task RememberedProfile_SurvivesManagerAndDatabaseContextRestartWithoutHidingPinRequirement()
    {
        var databasePath = Path.GetTempFileName();
        try
        {
            var firstRuntime = await CreateTestContextAsync(databasePath, ActivationDenial.None);
            await firstRuntime.Manager.ActivateProfileAsync(
                CreateActivationContext(firstRuntime.Owner.Id, firstRuntime.Profile.Id),
                TestContext.Current.CancellationToken);

            await using (var dbContext = CreateDbContext(databasePath))
            {
                var member = await dbContext.ProfileSelectorMembers.SingleAsync(
                    entity => entity.ProfileUserId.Equals(firstRuntime.Profile.Id),
                    TestContext.Current.CancellationToken);
                member.PinHash = "persisted-pin-hash";
                await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var restartedRuntime = CreateRuntime(databasePath, firstRuntime.Owner, firstRuntime.Profile, ActivationDenial.None);
            var selector = await restartedRuntime.Manager.GetCurrentSelectorAsync(
                firstRuntime.Profile.Id,
                DeviceId,
                false,
                TestContext.Current.CancellationToken);

            Assert.NotNull(selector);
            Assert.Equal(firstRuntime.Profile.Id, selector.CurrentDeviceProfileUserId);
            Assert.True(selector.CurrentDeviceProfileRequiresPin);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Theory]
    [InlineData(ActivationDenial.Hidden)]
    [InlineData(ActivationDenial.Disabled)]
    [InlineData(ActivationDenial.Schedule)]
    [InlineData(ActivationDenial.Device)]
    [InlineData(ActivationDenial.Locked)]
    [InlineData(ActivationDenial.Removed)]
    public async Task RememberedInvalidProfile_IsClearedAcrossManagerAndDatabaseContextRestart(ActivationDenial denial)
    {
        var databasePath = Path.GetTempFileName();
        try
        {
            var firstRuntime = await CreateTestContextAsync(databasePath, denial);
            await using (var dbContext = CreateDbContext(databasePath))
            {
                var selector = await dbContext.ProfileSelectors
                    .Include(entity => entity.Members)
                    .SingleAsync(TestContext.Current.CancellationToken);
                var member = await dbContext.ProfileSelectorMembers.SingleAsync(
                    entity => entity.ProfileUserId.Equals(firstRuntime.Profile.Id),
                    TestContext.Current.CancellationToken);
                if (denial is ActivationDenial.Locked)
                {
                    member.PinLockoutUntilUtc = DateTime.UtcNow.AddMinutes(1);
                }

                if (denial is ActivationDenial.Removed)
                {
                    selector.Members.Remove(member);
                    dbContext.ProfileSelectorMembers.Remove(member);
                }

                selector.DeviceStates.Add(new ProfileSelectorDeviceState(selector.Id, DeviceId)
                {
                    ActiveProfileUserId = firstRuntime.Profile.Id,
                    LastActivatedUtc = DateTime.UtcNow
                });
                await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var restartedRuntime = CreateRuntime(databasePath, firstRuntime.Owner, firstRuntime.Profile, denial);
            var selectorDto = await restartedRuntime.Manager.GetCurrentSelectorAsync(
                firstRuntime.Owner.Id,
                DeviceId,
                false,
                TestContext.Current.CancellationToken);

            Assert.NotNull(selectorDto);
            Assert.Null(selectorDto.CurrentDeviceProfileUserId);
            await using var verificationContext = CreateDbContext(databasePath);
            var persistedState = await verificationContext.ProfileSelectorDeviceStates.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Null(persistedState.ActiveProfileUserId);
            Assert.Null(persistedState.LastActivatedUtc);
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private static async Task<ManagerTestContext> CreateTestContextAsync(string databasePath, ActivationDenial denial)
    {
        var owner = CreateUser("selector-owner", isAdministrator: true);
        var profile = CreateUser("selector-profile", isAdministrator: false);
        ApplyDenial(profile, denial);

        await using (var dbContext = CreateDbContext(databasePath))
        {
            await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            dbContext.Users.AddRange(owner, profile);
            var selector = new ProfileSelector(owner.Id);
            selector.Members.Add(new ProfileSelectorMember(selector.Id, owner.Id));
            selector.Members.Add(new ProfileSelectorMember(selector.Id, profile.Id)
            {
                IsVisible = denial is not ActivationDenial.Hidden
            });
            dbContext.ProfileSelectors.Add(selector);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        return CreateRuntime(databasePath, owner, profile, denial);
    }

    private static ManagerTestContext CreateRuntime(string databasePath, User owner, User profile, ActivationDenial denial)
    {
        var dbContextFactory = new Mock<IDbContextFactory<JellyfinDbContext>>(MockBehavior.Strict);
        dbContextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .Returns((CancellationToken _) => Task.FromResult(CreateDbContext(databasePath)));

        var userManager = new Mock<IUserManager>(MockBehavior.Strict);
        userManager.Setup(manager => manager.GetUserById(owner.Id)).Returns(owner);
        userManager.Setup(manager => manager.GetUserById(profile.Id)).Returns(profile);
        userManager
            .Setup(manager => manager.GetUserDto(It.IsAny<User>(), It.IsAny<string?>()))
            .Returns((User user, string? _) => new UserDto
            {
                Id = user.Id,
                Name = user.Username,
                Policy = new UserPolicy
                {
                    BlockedMediaFolders = Array.Empty<Guid>()
                }
            });

        var deviceManager = new Mock<IDeviceManager>(MockBehavior.Strict);
        deviceManager
            .Setup(manager => manager.CanAccessDevice(profile, DeviceId))
            .Returns(denial is not ActivationDenial.Device);

        var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
        var activeSessions = denial is ActivationDenial.MaxSessions
            ? new[] { new SessionInfo(sessionManager.Object, NullLogger.Instance) { UserId = profile.Id } }
            : Array.Empty<SessionInfo>();
        sessionManager
            .SetupGet(manager => manager.Sessions)
            .Returns(activeSessions);
        sessionManager
            .Setup(manager => manager.AuthenticateDirect(It.IsAny<AuthenticationRequest>()))
            .ReturnsAsync(new AuthenticationResult
            {
                AccessToken = "runtime-token",
                User = new UserDto { Id = profile.Id, Name = profile.Username }
            });

        var networkManager = new Mock<INetworkManager>(MockBehavior.Strict);
        networkManager
            .Setup(manager => manager.IsInLocalNetwork(RemoteEndPoint))
            .Returns(denial is not ActivationDenial.Remote);

        var manager = new ProfileSelectorManager(
            dbContextFactory.Object,
            userManager.Object,
            deviceManager.Object,
            sessionManager.Object,
            Mock.Of<ICryptoProvider>(),
            networkManager.Object);

        return new ManagerTestContext(manager, sessionManager, owner, profile);
    }

    private static User CreateUser(string username, bool isAdministrator)
    {
        var user = new User(username, "test-auth-provider", "test-password-reset-provider");
        user.AddDefaultPermissions();
        user.AddDefaultPreferences();
        user.SetPermission(PermissionKind.IsAdministrator, isAdministrator);
        return user;
    }

    private static void ApplyDenial(User profile, ActivationDenial denial)
    {
        if (denial is ActivationDenial.Disabled)
        {
            profile.SetPermission(PermissionKind.IsDisabled, true);
        }

        if (denial is ActivationDenial.Remote)
        {
            profile.SetPermission(PermissionKind.EnableRemoteAccess, false);
        }

        if (denial is ActivationDenial.Schedule)
        {
            profile.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 25, 26, profile.Id));
        }

        if (denial is ActivationDenial.MaxSessions)
        {
            profile.MaxActiveSessions = 1;
        }
    }

    private static ProfileActivationContext CreateActivationContext(Guid ownerUserId, Guid profileUserId)
        => new()
        {
            CurrentUserId = ownerUserId,
            ProfileUserId = profileUserId,
            DeviceId = DeviceId,
            DeviceName = "Profile selector tests",
            Client = "Integration tests",
            Version = "1.0.0",
            RemoteEndPoint = RemoteEndPoint
        };

    private static JellyfinDbContext CreateDbContext(string databasePath)
    {
        var options = new DbContextOptionsBuilder<JellyfinDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        return new JellyfinDbContext(
            options,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
    }

    private sealed record ManagerTestContext(
        ProfileSelectorManager Manager,
        Mock<ISessionManager> SessionManager,
        User Owner,
        User Profile);
}
