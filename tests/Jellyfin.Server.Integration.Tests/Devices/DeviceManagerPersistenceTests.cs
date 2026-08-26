using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Devices;
using MediaBrowser.Controller.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Devices;

public sealed class DeviceManagerPersistenceTests
{
    [Fact]
    public async Task ReconcileDevice_DurableCredentialMissingFromCache_LoadsExactTokenIdempotently()
    {
        var databasePath = Path.GetTempFileName();
        try
        {
            JellyfinDbContext CreateDbContext()
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

            var user = new User("device-reconcile-user", "test", "test");
            user.AddDefaultPermissions();
            user.AddDefaultPreferences();
            await using (var setupContext = CreateDbContext())
            {
                await setupContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                setupContext.Users.Add(user);
                await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var contextFactory = new Mock<IDbContextFactory<JellyfinDbContext>>(MockBehavior.Strict);
            contextFactory.Setup(factory => factory.CreateDbContext()).Returns(CreateDbContext);
            contextFactory
                .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken _) => Task.FromResult(CreateDbContext()));
            var deviceManager = new DeviceManager(contextFactory.Object, Mock.Of<IUserManager>());
            var switchId = Guid.NewGuid();
            var durableDevice = new Device(user.Id, "test", "1", "device", "device-id")
            {
                ProfileSwitchId = switchId
            };
            await using (var commitContext = CreateDbContext())
            {
                commitContext.Devices.Add(durableDevice);
                await commitContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            Assert.Empty(deviceManager.GetDevices(new DeviceQuery { AccessToken = durableDevice.AccessToken }).Items);
            var reconciled = await deviceManager.ReconcileDevice(durableDevice.Id);
            var replay = await deviceManager.ReconcileDevice(durableDevice.Id);
            Assert.NotNull(reconciled);
            Assert.NotNull(replay);

            Assert.Equal(durableDevice.Id, reconciled!.Id);
            Assert.Equal(durableDevice.AccessToken, reconciled.AccessToken);
            Assert.Equal(switchId, reconciled.ProfileSwitchId);
            Assert.Equal(reconciled.AccessToken, replay!.AccessToken);
            Assert.Same(
                replay,
                Assert.Single(deviceManager.GetDevices(new DeviceQuery { AccessToken = durableDevice.AccessToken }).Items));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    [Fact]
    public async Task DeleteDevice_DatabaseFailureKeepsCredentialInRuntimeCacheAndConcurrentRetryConverges()
    {
        var databasePath = Path.GetTempFileName();
        var failureInterceptor = new DeviceDeleteFailureInterceptor();
        try
        {
            JellyfinDbContext CreateDbContext()
            {
                var options = new DbContextOptionsBuilder<JellyfinDbContext>()
                    .UseSqlite($"Data Source={databasePath}")
                    .AddInterceptors(failureInterceptor)
                    .Options;
                return new JellyfinDbContext(
                    options,
                    NullLogger<JellyfinDbContext>.Instance,
                    new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
                    new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
            }

            var user = new User("device-delete-user", "test", "test");
            user.AddDefaultPermissions();
            user.AddDefaultPreferences();
            var device = new Device(user.Id, "test", "1", "device", "device-id");
            await using (var setupContext = CreateDbContext())
            {
                await setupContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
                setupContext.Users.Add(user);
                setupContext.Devices.Add(device);
                await setupContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            var contextFactory = new Mock<IDbContextFactory<JellyfinDbContext>>(MockBehavior.Strict);
            contextFactory.Setup(factory => factory.CreateDbContext()).Returns(CreateDbContext);
            contextFactory
                .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken _) => Task.FromResult(CreateDbContext()));
            var deviceManager = new DeviceManager(contextFactory.Object, Mock.Of<IUserManager>());
            var cachedDevice = Assert.Single(
                deviceManager.GetDevices(new DeviceQuery { AccessToken = device.AccessToken }).Items);

            failureInterceptor.FailDeletes = true;
            await Assert.ThrowsAsync<DbUpdateException>(() => deviceManager.DeleteDevice(cachedDevice));

            Assert.Single(deviceManager.GetDevices(new DeviceQuery { AccessToken = device.AccessToken }).Items);
            await using (var failedDeleteContext = CreateDbContext())
            {
                Assert.True(await failedDeleteContext.Devices.AnyAsync(
                    candidate => candidate.AccessToken == device.AccessToken,
                    TestContext.Current.CancellationToken));
            }

            failureInterceptor.FailDeletes = false;
            await Task.WhenAll(
                deviceManager.DeleteDevice(cachedDevice),
                deviceManager.DeleteDevice(cachedDevice));

            Assert.Empty(deviceManager.GetDevices(new DeviceQuery { AccessToken = device.AccessToken }).Items);
            await using var deletedContext = CreateDbContext();
            Assert.False(await deletedContext.Devices.AnyAsync(
                candidate => candidate.AccessToken == device.AccessToken,
                TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(databasePath);
        }
    }

    private sealed class DeviceDeleteFailureInterceptor : SaveChangesInterceptor
    {
        public bool FailDeletes { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailDeletes
                && eventData.Context!.ChangeTracker.Entries<Device>().Any(entry => entry.State is EntityState.Deleted))
            {
                throw new DbUpdateException("Injected device delete failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
