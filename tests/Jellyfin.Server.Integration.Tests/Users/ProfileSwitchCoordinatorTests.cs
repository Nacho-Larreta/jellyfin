using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Cryptography;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.Devices;
using Jellyfin.Server.Implementations.Users;
using Jellyfin.Server.Implementations.Users.Persistence;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using MediaBrowser.Model.Users;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Users;

public sealed class ProfileSwitchCoordinatorTests
{
    private const string DeviceId = "profile-switch-device";
    private const string RemoteEndPoint = "192.0.2.10";

    public enum MutableCommitDenial
    {
        Hidden,
        Disabled,
        Locked,
        Removed,
        Remote,
        Schedule,
        Device,
        MaxSessions,
        SelectorDisabled
    }

    [Fact]
    public async Task Prepare_IsDurableNonMutatingIdempotentAndDeviceExclusive()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();

        var prepared = await runtime.Coordinator.PrepareAsync(
            runtime.CreatePrepare(switchId),
            TestContext.Current.CancellationToken);
        var replay = await runtime.Coordinator.PrepareAsync(
            runtime.CreatePrepare(switchId),
            TestContext.Current.CancellationToken);
        var restartedCoordinator = runtime.CreateCoordinator();
        var restartedStatus = await restartedCoordinator.GetStatusAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Prepared, prepared.State);
        Assert.Equal(ProfileSwitchState.Prepared, replay.State);
        Assert.Equal(ProfileSwitchState.Prepared, restartedStatus.State);
        Assert.Null(prepared.AuthenticationResult);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), It.IsAny<Guid>()),
            Times.Never);

        await using (var verificationContext = runtime.CreateDbContext())
        {
            var operation = await verificationContext.ProfileSelectorSwitchOperations.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ProfileSelectorSwitchOperationState.Prepared, operation.State);
            Assert.Equal(DeviceId, operation.ActiveDeviceId);
            Assert.Null(operation.AuthenticationDeviceRecordId);
            Assert.Null(operation.PinProofHash);
            Assert.DoesNotContain("pin", operation.DeviceName, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(verificationContext.ProfileSelectorDeviceStates);
        }

        var conflict = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.PrepareAsync(
                runtime.CreatePrepare(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_ALREADY_IN_PROGRESS", conflict.ErrorCode);
    }

    [Fact]
    public async Task Prepare_ConflictingSwitchIdPayloadOrBoundDeviceIsRejected()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);

        var targetConflict = runtime.CreatePrepare(switchId);
        targetConflict.TargetProfileUserId = runtime.Owner.Id;
        var targetException = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.PrepareAsync(targetConflict, TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_ID_CONFLICT", targetException.ErrorCode);

        var otherDeviceRequest = runtime.CreatePrepare(switchId);
        otherDeviceRequest.RequestContext!.DeviceId = "other-device";
        var deviceException = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.PrepareAsync(otherDeviceRequest, TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_NOT_FOUND", deviceException.ErrorCode);

        var otherCredentialContext = runtime.RequestContext;
        otherCredentialContext.CallerCredentialRecordId++;
        var credentialException = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.GetStatusAsync(
                switchId,
                otherCredentialContext,
                TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_NOT_FOUND", credentialException.ErrorCode);
    }

    [Fact]
    public async Task Prepare_SecondaryMemberCannotSwitchToItsAlreadyActiveIdentity()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var prepare = runtime.CreatePrepare(Guid.NewGuid());
        prepare.RequestContext = runtime.CreateRequestContext(runtime.Target.Id);
        prepare.TargetProfileUserId = runtime.Target.Id;

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.PrepareAsync(prepare, TestContext.Current.CancellationToken));

        Assert.Equal("PROFILE_SWITCH_ALREADY_ACTIVE", exception.ErrorCode);
        await using var dbContext = runtime.CreateDbContext();
        Assert.Empty(dbContext.ProfileSelectorSwitchOperations);
    }

    [Fact]
    public async Task Prepare_ValidatesAndHashesPinWithoutPersistingPlaintext()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        await runtime.SetTargetPinAsync("0123");

        foreach (var invalidPin in new[] { "123", "123456789", "１２３４", "12a4" })
        {
            var invalid = runtime.CreatePrepare(Guid.NewGuid());
            invalid.Pin = invalidPin;
            var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
                () => runtime.Coordinator.PrepareAsync(invalid, TestContext.Current.CancellationToken));
            Assert.Equal("PROFILE_PIN_INVALID_FORMAT", exception.ErrorCode);
        }

        var wrong = runtime.CreatePrepare(Guid.NewGuid());
        wrong.Pin = "9999";
        var wrongPin = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.PrepareAsync(wrong, TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_PIN_INVALID", wrongPin.ErrorCode);

        var switchId = Guid.NewGuid();
        var correct = runtime.CreatePrepare(switchId);
        correct.Pin = "0123";
        var prepared = await runtime.Coordinator.PrepareAsync(correct, TestContext.Current.CancellationToken);
        var replay = await runtime.Coordinator.PrepareAsync(correct, TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchState.Prepared, prepared.State);
        Assert.Equal(ProfileSwitchState.Prepared, replay.State);

        var differentPin = runtime.CreatePrepare(switchId);
        differentPin.Pin = "9999";
        var conflict = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.PrepareAsync(differentPin, TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_ID_CONFLICT", conflict.ErrorCode);

        await using var dbContext = runtime.CreateDbContext();
        var operation = await dbContext.ProfileSelectorSwitchOperations.SingleAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(operation.PinProofHash);
        Assert.DoesNotContain("0123", operation.PinProofHash, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreparedOperation_ExpiresDeterministicallyAndReleasesDeviceSlot()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);

        runtime.Clock.Advance(ProfileSwitchOptions.DefaultPreparedLifetime);
        var status = await runtime.CreateCoordinator().GetStatusAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Expired, status.State);
        var expired = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.CommitAsync(switchId, runtime.RequestContext, TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_EXPIRED", expired.ErrorCode);

        var next = await runtime.Coordinator.PrepareAsync(
            runtime.CreatePrepare(Guid.NewGuid()),
            TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchState.Prepared, next.State);
    }

    [Fact]
    public async Task Prepare_EnforcesPerCallerDeviceQuotaAndGlobalRetentionPurgeReopensCapacity()
    {
        var options = new ProfileSwitchOptions
        {
            MaxRetainedOperationsPerCallerDevice = 1
        };
        await using var runtime = await SwitchRuntime.CreateAsync(options: options);
        var firstSwitchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(firstSwitchId), TestContext.Current.CancellationToken);
        await runtime.Coordinator.AbortAsync(firstSwitchId, runtime.RequestContext, TestContext.Current.CancellationToken);

        var limited = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.PrepareAsync(
                runtime.CreatePrepare(Guid.NewGuid()),
                TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_OPERATION_LIMIT", limited.ErrorCode);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, limited.StatusCode);

        runtime.Clock.Advance(options.TerminalRetention);
        var afterPurge = await runtime.CreateCoordinator().PrepareAsync(
            runtime.CreatePrepare(Guid.NewGuid()),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Prepared, afterPurge.State);
        await using var dbContext = runtime.CreateDbContext();
        Assert.Single(dbContext.ProfileSelectorSwitchOperations);
    }

    [Fact]
    public async Task Commit_IsAtomicAndReplayableAcrossCoordinatorRestartWithoutRevokingRecoveryCredential()
    {
        await using var runtime = await SwitchRuntime.CreateAsync(includeRecoveryCredential: true);
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);

        var committed = await runtime.Coordinator.CommitAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);
        var restartedCoordinator = runtime.CreateCoordinator();
        var statusReplay = await restartedCoordinator.GetStatusAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);
        var commitReplay = await restartedCoordinator.CommitAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Committed, committed.State);
        Assert.Equal(committed.AuthenticationResult!.AccessToken, statusReplay.AuthenticationResult!.AccessToken);
        Assert.Equal(committed.AuthenticationResult.AccessToken, commitReplay.AuthenticationResult!.AccessToken);
        Assert.Contains(runtime.Devices, device => device.UserId.Equals(runtime.Owner.Id) && !device.ProfileSwitchId.HasValue);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), switchId),
            Times.Once);

        await using var verificationContext = runtime.CreateDbContext();
        var operation = await verificationContext.ProfileSelectorSwitchOperations.SingleAsync(TestContext.Current.CancellationToken);
        var deviceState = await verificationContext.ProfileSelectorDeviceStates.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorSwitchOperationState.Committed, operation.State);
        Assert.Null(operation.ActiveDeviceId);
        Assert.NotNull(operation.AuthenticationDeviceRecordId);
        Assert.Equal(runtime.Target.Id, deviceState.ActiveProfileUserId);
    }

    [Fact]
    public async Task CommittedResult_SurvivesReconstructedStoreDeviceManagerAndCoordinator()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var firstProcess = runtime.CreatePersistedDeviceCoordinator();
        using var firstCoordinator = firstProcess.Coordinator;
        var switchId = Guid.NewGuid();
        await firstCoordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var committed = await firstCoordinator.CommitAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        var restartedProcess = runtime.CreatePersistedDeviceCoordinator();
        using var restartedCoordinator = restartedProcess.Coordinator;
        var recovered = await restartedCoordinator.GetStatusAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Committed, recovered.State);
        Assert.Equal(committed.AuthenticationResult!.AccessToken, recovered.AuthenticationResult!.AccessToken);
        firstProcess.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), switchId),
            Times.Once);
        restartedProcess.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task Commit_WhenRuntimeRegistrationFailsAfterDurableCommit_StatusRecoversExactTokenInSameProcess()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        runtime.SessionManager
            .Setup(manager => manager.RegisterCommittedProfileSwitchCredential(It.IsAny<Device>()))
            .Throws(new InvalidOperationException("Injected runtime-cache registration failure."));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Coordinator.CommitAsync(
                switchId,
                runtime.RequestContext,
                TestContext.Current.CancellationToken));

        string persistedToken;
        int persistedDeviceId;
        await using (var verificationContext = runtime.CreateDbContext())
        {
            var operation = await verificationContext.ProfileSelectorSwitchOperations.SingleAsync(
                TestContext.Current.CancellationToken);
            var credential = await verificationContext.Devices.SingleAsync(
                device => device.ProfileSwitchId.Equals(switchId),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProfileSelectorSwitchOperationState.Committed, operation.State);
            Assert.Equal(credential.Id, operation.AuthenticationDeviceRecordId);
            persistedToken = credential.AccessToken;
            persistedDeviceId = credential.Id;
        }

        var recovered = await runtime.Coordinator.GetStatusAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Committed, recovered.State);
        Assert.Equal(persistedToken, recovered.AuthenticationResult!.AccessToken);
        Assert.Contains(
            runtime.Devices,
            device => device.Id == persistedDeviceId && device.AccessToken == persistedToken);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), switchId),
            Times.Once);
    }

    [Fact]
    public async Task CommittedResult_WhenReplacementCacheIsMissing_ConcurrentStatusAndCommitReconcileBeforeCleanup()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var supersededCredential = await runtime.AddTargetCredentialAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        runtime.FailSupersededCredentialCleanup = true;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => runtime.Coordinator.CommitAsync(
                switchId,
                runtime.RequestContext,
                TestContext.Current.CancellationToken));

        string persistedToken;
        int persistedDeviceId;
        await using (var verificationContext = runtime.CreateDbContext())
        {
            var operation = await verificationContext.ProfileSelectorSwitchOperations.SingleAsync(
                TestContext.Current.CancellationToken);
            var replacementCredential = await verificationContext.Devices.SingleAsync(
                device => device.ProfileSwitchId.Equals(switchId),
                TestContext.Current.CancellationToken);
            Assert.Equal(ProfileSelectorSwitchOperationState.Committed, operation.State);
            Assert.Equal(replacementCredential.Id, operation.AuthenticationDeviceRecordId);
            persistedToken = replacementCredential.AccessToken;
            persistedDeviceId = replacementCredential.Id;
        }

        runtime.FailSupersededCredentialCleanup = false;
        runtime.RemoveCachedCredential(persistedDeviceId);
        using var competingCoordinator = runtime.CreateCoordinator();

        var recoveredResults = await Task.WhenAll(
            runtime.Coordinator.GetStatusAsync(
                switchId,
                runtime.RequestContext,
                TestContext.Current.CancellationToken),
            competingCoordinator.CommitAsync(
                switchId,
                runtime.RequestContext,
                TestContext.Current.CancellationToken));

        Assert.All(recoveredResults, result => Assert.Equal(ProfileSwitchState.Committed, result.State));
        Assert.All(
            recoveredResults,
            result => Assert.Equal(persistedToken, result.AuthenticationResult!.AccessToken));
        Assert.Contains(
            runtime.Devices,
            device => device.Id == persistedDeviceId && device.AccessToken == persistedToken);
        Assert.DoesNotContain(runtime.Devices, device => device.Id == supersededCredential.Id);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), switchId),
            Times.Once);
    }

    [Theory]
    [InlineData(MutableCommitDenial.Hidden, "PROFILE_NOT_VISIBLE")]
    [InlineData(MutableCommitDenial.Disabled, "PROFILE_USER_DISABLED")]
    [InlineData(MutableCommitDenial.Locked, "PROFILE_PIN_LOCKED")]
    [InlineData(MutableCommitDenial.Removed, "PROFILE_NOT_LINKED")]
    [InlineData(MutableCommitDenial.Remote, "PROFILE_REMOTE_ACCESS_DENIED")]
    [InlineData(MutableCommitDenial.Schedule, "PROFILE_ACCESS_SCHEDULE_DENIED")]
    [InlineData(MutableCommitDenial.Device, "PROFILE_DEVICE_ACCESS_DENIED")]
    [InlineData(MutableCommitDenial.MaxSessions, "PROFILE_MAX_SESSIONS_REACHED")]
    [InlineData(MutableCommitDenial.SelectorDisabled, "PROFILE_SELECTOR_NOT_CONFIGURED")]
    public async Task Commit_RevalidatesMutablePolicyAndAbortsBeforeAuthentication(
        MutableCommitDenial denial,
        string expectedErrorCode)
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        await runtime.ApplyCommitDenialAsync(denial);

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.CommitAsync(switchId, runtime.RequestContext, TestContext.Current.CancellationToken));

        Assert.Equal(expectedErrorCode, exception.ErrorCode);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), It.IsAny<Guid>()),
            Times.Never);
        await using var verificationContext = runtime.CreateDbContext();
        var operation = await verificationContext.ProfileSelectorSwitchOperations.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorSwitchOperationState.Aborted, operation.State);
        Assert.Null(operation.ActiveDeviceId);
        Assert.Empty(verificationContext.ProfileSelectorDeviceStates);
    }

    [Fact]
    public async Task Commit_RevalidatesBoundCallerMembershipButStatusRemainsRecoverableToBoundCaller()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        var memberRequest = runtime.CreatePrepare(switchId);
        memberRequest.RequestContext = runtime.CreateRequestContext(runtime.Target.Id);
        memberRequest.TargetProfileUserId = runtime.Owner.Id;
        await runtime.Coordinator.PrepareAsync(memberRequest, TestContext.Current.CancellationToken);

        await runtime.RemoveTargetMembershipAsync();
        var status = await runtime.Coordinator.GetStatusAsync(
            switchId,
            memberRequest.RequestContext,
            TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchState.Prepared, status.State);

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.CommitAsync(
                switchId,
                memberRequest.RequestContext,
                TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_CALLER_NOT_AUTHORIZED", exception.ErrorCode);
    }

    [Fact]
    public async Task Commit_DatabaseFailureRollsBackCredentialAndSameSwitchCanRetry()
    {
        var failureInterceptor = new CommitFailureInterceptor();
        await using var runtime = await SwitchRuntime.CreateAsync(saveChangesInterceptor: failureInterceptor);
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        failureInterceptor.FailCommittedOperation = true;

        await Assert.ThrowsAsync<DbUpdateException>(
            () => runtime.Coordinator.CommitAsync(switchId, runtime.RequestContext, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(runtime.Devices, device => device.ProfileSwitchId.Equals(switchId));
        await using (var failedContext = runtime.CreateDbContext())
        {
            var operation = await failedContext.ProfileSelectorSwitchOperations.SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ProfileSelectorSwitchOperationState.Prepared, operation.State);
            Assert.Empty(failedContext.ProfileSelectorDeviceStates);
            Assert.DoesNotContain(
                failedContext.Devices,
                device => device.ProfileSwitchId.Equals(switchId));
        }

        failureInterceptor.FailCommittedOperation = false;
        var recovered = await runtime.CreateCoordinator().CommitAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Committed, recovered.State);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), switchId),
            Times.Exactly(2));
        runtime.SessionManager.Verify(manager => manager.RevokeProfileSwitchCredential(It.IsAny<Device>()), Times.Never);
    }

    [Fact]
    public async Task Commit_RevalidatesAgainAfterCredentialCreationAndBeforeCommitPoint()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        runtime.AfterCredentialCreated = runtime.DisableTargetDurably;

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.CommitAsync(
                switchId,
                runtime.RequestContext,
                TestContext.Current.CancellationToken));

        Assert.Equal("PROFILE_USER_DISABLED", exception.ErrorCode);
        Assert.DoesNotContain(runtime.Devices, device => device.ProfileSwitchId.Equals(switchId));
        await using var dbContext = runtime.CreateDbContext();
        var operation = await dbContext.ProfileSelectorSwitchOperations.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorSwitchOperationState.Aborted, operation.State);
        Assert.Empty(dbContext.ProfileSelectorDeviceStates);
    }

    [Fact]
    public async Task Commit_RevalidatesSessionLimitUnderHeldAdmissionBeforeCommitPoint()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        await runtime.SetTargetMaxActiveSessionsAsync(1);
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        runtime.AfterCredentialCreated = () => runtime.AddSession(runtime.Target.Id, "other-device");

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.CommitAsync(
                switchId,
                runtime.RequestContext,
                TestContext.Current.CancellationToken));

        Assert.Equal("PROFILE_MAX_SESSIONS_REACHED", exception.ErrorCode);
        await using var dbContext = runtime.CreateDbContext();
        var operation = await dbContext.ProfileSelectorSwitchOperations.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorSwitchOperationState.Aborted, operation.State);
        Assert.DoesNotContain(dbContext.Devices, device => device.ProfileSwitchId.Equals(switchId));
    }

    [Fact]
    public async Task ConcurrentDistinctPrepareAndSameSwitchCommitConvergeDeterministically()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var firstSwitchId = Guid.NewGuid();
        var secondSwitchId = Guid.NewGuid();
        using var competingCoordinator = runtime.CreateCoordinator();

        var prepareOutcomes = await Task.WhenAll(
            CapturePrepareAsync(runtime, firstSwitchId),
            CapturePrepareAsync(runtime, competingCoordinator, secondSwitchId));
        Assert.Single(prepareOutcomes, outcome => outcome.State is ProfileSwitchState.Prepared);
        Assert.Single(prepareOutcomes, outcome => outcome.ErrorCode is "PROFILE_SWITCH_ALREADY_IN_PROGRESS");

        var preparedSwitchId = prepareOutcomes.Single(outcome => outcome.State is ProfileSwitchState.Prepared).SwitchId;
        var commitResults = await Task.WhenAll(
            runtime.Coordinator.CommitAsync(preparedSwitchId, runtime.RequestContext, TestContext.Current.CancellationToken),
            competingCoordinator.CommitAsync(preparedSwitchId, runtime.RequestContext, TestContext.Current.CancellationToken));

        Assert.All(commitResults, result => Assert.Equal(ProfileSwitchState.Committed, result.State));
        Assert.Equal(commitResults[0].AuthenticationResult!.AccessToken, commitResults[1].AuthenticationResult!.AccessToken);
        await using var verificationContext = runtime.CreateDbContext();
        Assert.Single(
            verificationContext.Devices,
            device => device.ProfileSwitchId.Equals(preparedSwitchId));
    }

    [Fact]
    public async Task PlaybackStop_DeduplicatesAcrossRestartAndRejectsChangedOutcome()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.StartOldPlayback();

        var first = await runtime.Coordinator.ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            playbackStop,
            TestContext.Current.CancellationToken);
        var replay = await runtime.CreateCoordinator().ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            playbackStop,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchPlaybackStopOutcome.Acknowledged, first.Outcome);
        Assert.Equal(first.ReportKey, replay.ReportKey);
        runtime.SessionManager.Verify(
            manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()),
            Times.Once);
        runtime.TranscodeManager.Verify(
            manager => manager.KillTranscodingJobs(DeviceId, playbackStop.PlaySessionId, It.IsAny<Func<string, bool>>()),
            Times.Once);

        var changedPosition = runtime.StartOldPlayback();
        changedPosition.PositionTicks++;
        var conflict = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                changedPosition,
                TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_PLAYBACK_REPORT_CONFLICT", conflict.ErrorCode);
    }

    [Fact]
    public async Task PlaybackStop_ConcurrentDuplicateRequestsHaveOneLogicalSideEffect()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.StartOldPlayback();

        using var competingCoordinator = runtime.CreateCoordinator();
        var outcomes = await Task.WhenAll(
            runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                playbackStop,
                TestContext.Current.CancellationToken),
            competingCoordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                playbackStop,
                TestContext.Current.CancellationToken));

        Assert.All(outcomes, outcome => Assert.Equal(ProfileSwitchPlaybackStopOutcome.Acknowledged, outcome.Outcome));
        Assert.Equal(outcomes[0].ReportKey, outcomes[1].ReportKey);
        runtime.SessionManager.Verify(
            manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()),
            Times.Once);
        runtime.TranscodeManager.Verify(
            manager => manager.KillTranscodingJobs(DeviceId, playbackStop.PlaySessionId, It.IsAny<Func<string, bool>>()),
            Times.Once);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PlaybackStop_ItemOrPlaySessionMismatchPersistsFailedReceipt(bool mismatchItem)
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.StartOldPlayback();
        if (mismatchItem)
        {
            playbackStop.ItemId = Guid.NewGuid();
        }
        else
        {
            playbackStop.PlaySessionId = "spoofed-play-session";
        }

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                playbackStop,
                TestContext.Current.CancellationToken));

        Assert.Equal("PROFILE_SWITCH_PLAYBACK_SNAPSHOT_MISMATCH", exception.ErrorCode);
        runtime.SessionManager.Verify(
            manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()),
            Times.Once);
        runtime.TranscodeManager.Verify(
            manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()),
            Times.Never);
        await using var dbContext = runtime.CreateDbContext();
        var receipt = await dbContext.ProfileSelectorPlaybackStopReports.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorPlaybackStopReportState.Failed, receipt.State);
    }

    [Fact]
    public async Task PlaybackStop_WhenCapturedAAdvancedToB_PreservesBAndPersistsFailedReceipt()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var capturedPlaybackA = runtime.StartOldPlayback();
        var activeSession = Assert.Single(runtime.SessionManager.Object.Sessions);
        var playbackB = new BaseItemDto { Id = Guid.NewGuid() };
        activeSession.NowPlayingItem = playbackB;
        activeSession.PlaySessionId = "replacement-play-session";

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                capturedPlaybackA,
                TestContext.Current.CancellationToken));

        Assert.Equal("PROFILE_SWITCH_PLAYBACK_SNAPSHOT_MISMATCH", exception.ErrorCode);
        Assert.Same(playbackB, activeSession.NowPlayingItem);
        Assert.Equal("replacement-play-session", activeSession.PlaySessionId);
        runtime.TranscodeManager.Verify(
            manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()),
            Times.Never);
        await using var dbContext = runtime.CreateDbContext();
        var receipt = await dbContext.ProfileSelectorPlaybackStopReports.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorPlaybackStopReportState.Failed, receipt.State);
    }

    [Fact]
    public async Task PlaybackStop_DoesNotTouchPlaybackOnAnotherDevice()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.StartPlaybackFor(runtime.Owner.Id, "other-device");

        var result = await runtime.Coordinator.ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            playbackStop,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchPlaybackStopOutcome.NotActive, result.Outcome);
        Assert.NotNull(runtime.SessionManager.Object.Sessions.Single().NowPlayingItem);
        await using var dbContext = runtime.CreateDbContext();
        var receipt = await dbContext.ProfileSelectorPlaybackStopReports.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorPlaybackStopReportState.NotActive, receipt.State);
    }

    [Fact]
    public async Task PlaybackStop_UsesCanonicalOperationIdentityForAtomicSessionStop()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.StartOldPlayback();
        playbackStop.MediaSourceId = "client-media-source";
        playbackStop.LiveStreamId = "client-live-stream";
        playbackStop.PlaylistItemId = "client-playlist-item";
        playbackStop.NowPlayingQueue = [new QueueItem { Id = Guid.NewGuid(), PlaylistItemId = "client-queue-item" }];

        var result = await runtime.Coordinator.ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            playbackStop,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchPlaybackStopOutcome.Acknowledged, result.Outcome);
        runtime.SessionManager.Verify(
            manager => manager.StopProfileSwitchPlaybackAsync(It.Is<ProfileSwitchSessionStopRequest>(request =>
                request.UserId.Equals(runtime.Owner.Id)
                && request.DeviceId == DeviceId
                && request.Client == runtime.RequestContext.Client
                && request.ItemId.Equals(playbackStop.ItemId)
                && request.PlaySessionId == "captured-play-session")),
            Times.Once);
    }

    [Fact]
    public async Task PlaybackStop_EnforcesReceiptQuotaAfterAllowingExactReplay()
    {
        var options = new ProfileSwitchOptions
        {
            MaxPlaybackReportsPerSwitch = 1
        };
        await using var runtime = await SwitchRuntime.CreateAsync(options: options);
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var firstReport = runtime.CreatePlaybackStop();

        var first = await runtime.Coordinator.ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            firstReport,
            TestContext.Current.CancellationToken);
        var replay = await runtime.Coordinator.ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            firstReport,
            TestContext.Current.CancellationToken);
        var secondReport = runtime.CreatePlaybackStop();
        secondReport.PlaySessionId = "second-play-session";
        var limited = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                secondReport,
                TestContext.Current.CancellationToken));

        Assert.Equal(ProfileSwitchPlaybackStopOutcome.NotActive, first.Outcome);
        Assert.Equal(first.ReportKey, replay.ReportKey);
        Assert.Equal("PROFILE_SWITCH_PLAYBACK_REPORT_LIMIT", limited.ErrorCode);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, limited.StatusCode);
        await using var dbContext = runtime.CreateDbContext();
        Assert.Single(dbContext.ProfileSelectorPlaybackStopReports);
    }

    [Fact]
    public async Task PlaybackStop_ConcurrentDistinctReportsCannotExceedReceiptQuota()
    {
        var options = new ProfileSwitchOptions
        {
            MaxPlaybackReportsPerSwitch = 1
        };
        await using var runtime = await SwitchRuntime.CreateAsync(options: options);
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var firstReport = runtime.CreatePlaybackStop();
        var secondReport = runtime.CreatePlaybackStop();
        secondReport.PlaySessionId = "concurrent-play-session";

        using var competingCoordinator = runtime.CreateCoordinator();
        var exceptions = await Task.WhenAll(
            Record.ExceptionAsync(() => runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                firstReport,
                TestContext.Current.CancellationToken)).AsTask(),
            Record.ExceptionAsync(() => competingCoordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                secondReport,
                TestContext.Current.CancellationToken)).AsTask());

        Assert.Single(exceptions, exception => exception is null);
        var limited = Assert.Single(exceptions.OfType<ProfileSelectorException>());
        Assert.Equal("PROFILE_SWITCH_PLAYBACK_REPORT_LIMIT", limited.ErrorCode);
        Assert.Equal((int)HttpStatusCode.TooManyRequests, limited.StatusCode);
        await using var dbContext = runtime.CreateDbContext();
        Assert.Single(dbContext.ProfileSelectorPlaybackStopReports);
    }

    [Fact]
    public async Task PlaybackStop_RejectsOversizedPayloadBeforeCreatingReceipt()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var oversizedText = runtime.CreatePlaybackStop();
        oversizedText.NextMediaType = new string('x', 33);

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                oversizedText,
                TestContext.Current.CancellationToken));
        Assert.Equal("PROFILE_SWITCH_PLAYBACK_REPORT_INVALID", exception.ErrorCode);

        await using var dbContext = runtime.CreateDbContext();
        Assert.Empty(dbContext.ProfileSelectorPlaybackStopReports);
    }

    [Fact]
    public async Task PlaybackStop_MissingOrInterruptedOldSessionIsClassifiedWithoutDuplicateSideEffect()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.CreatePlaybackStop();

        var notActive = await runtime.Coordinator.ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            playbackStop,
            TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchPlaybackStopOutcome.NotActive, notActive.Outcome);
        runtime.SessionManager.Verify(
            manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()),
            Times.Once);

        await using var dbContext = runtime.CreateDbContext();
        var receipt = await dbContext.ProfileSelectorPlaybackStopReports.SingleAsync(TestContext.Current.CancellationToken);
        receipt.State = ProfileSelectorPlaybackStopReportState.Processing;
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        var interrupted = await runtime.CreateCoordinator().ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            playbackStop,
            TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSwitchPlaybackStopOutcome.Failed, interrupted.Outcome);
        runtime.SessionManager.Verify(
            manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()),
            Times.Once);
    }

    [Fact]
    public async Task PlaybackStop_RejectsAndPersistsFailureForAnotherUsersSession()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.StartPlaybackFor(runtime.Target.Id, DeviceId);

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.ReportPlaybackStoppedAsync(
                switchId,
                runtime.RequestContext,
                playbackStop,
                TestContext.Current.CancellationToken));

        Assert.Equal("PROFILE_SWITCH_PLAYBACK_SESSION_MISMATCH", exception.ErrorCode);
        runtime.SessionManager.Verify(
            manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()),
            Times.Once);
        await using var dbContext = runtime.CreateDbContext();
        var receipt = await dbContext.ProfileSelectorPlaybackStopReports.SingleAsync(TestContext.Current.CancellationToken);
        Assert.Equal(ProfileSelectorPlaybackStopReportState.Failed, receipt.State);
        Assert.Equal(runtime.Owner.Id, receipt.CallerUserId);
    }

    [Fact]
    public async Task Commit_IsBlockedByAnUnclassifiedPlaybackReport()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);
        var playbackStop = runtime.CreatePlaybackStop();
        await runtime.Coordinator.ReportPlaybackStoppedAsync(
            switchId,
            runtime.RequestContext,
            playbackStop,
            TestContext.Current.CancellationToken);

        await using (var dbContext = runtime.CreateDbContext())
        {
            var receipt = await dbContext.ProfileSelectorPlaybackStopReports.SingleAsync(TestContext.Current.CancellationToken);
            receipt.State = ProfileSelectorPlaybackStopReportState.Processing;
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var exception = await Assert.ThrowsAsync<ProfileSelectorException>(
            () => runtime.Coordinator.CommitAsync(
                switchId,
                runtime.RequestContext,
                TestContext.Current.CancellationToken));

        Assert.Equal("PROFILE_SWITCH_PLAYBACK_NOT_SETTLED", exception.ErrorCode);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), It.IsAny<Guid>()),
            Times.Never);
    }

    [Fact]
    public async Task Commit_ReplacesSameDeviceTargetSessionWithoutCountingRecoveryAgainstTheLimit()
    {
        await using var runtime = await SwitchRuntime.CreateAsync();
        runtime.Target.MaxActiveSessions = 1;
        runtime.AddSession(runtime.Target.Id, DeviceId);
        var switchId = Guid.NewGuid();
        await runtime.Coordinator.PrepareAsync(runtime.CreatePrepare(switchId), TestContext.Current.CancellationToken);

        var committed = await runtime.Coordinator.CommitAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(ProfileSwitchState.Committed, committed.State);
        runtime.SessionManager.Verify(
            manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), switchId),
            Times.Once);
    }

    [Fact]
    public async Task Commit_ToOwnerCreatesRuntimeCredentialAndPreservesRecoveryCredential()
    {
        await using var runtime = await SwitchRuntime.CreateAsync(includeRecoveryCredential: true);
        runtime.Owner.MaxActiveSessions = 1;
        runtime.AddSession(runtime.Owner.Id, DeviceId);
        var switchId = Guid.NewGuid();
        var prepare = runtime.CreatePrepare(switchId);
        prepare.TargetProfileUserId = runtime.Owner.Id;
        await runtime.Coordinator.PrepareAsync(prepare, TestContext.Current.CancellationToken);

        var committed = await runtime.Coordinator.CommitAsync(
            switchId,
            runtime.RequestContext,
            TestContext.Current.CancellationToken);

        Assert.Equal(runtime.Owner.Id, committed.AuthenticationResult!.User.Id);
        Assert.Contains(
            runtime.Devices,
            device => device.UserId.Equals(runtime.Owner.Id) && !device.ProfileSwitchId.HasValue);
        Assert.Contains(
            runtime.Devices,
            device => device.UserId.Equals(runtime.Owner.Id) && device.ProfileSwitchId.Equals(switchId));
        runtime.SessionManager.Verify(
            manager => manager.RevokeSupersededProfileCredential(It.IsAny<Device>(), It.IsAny<Guid>()),
            Times.Never);
    }

    private static async Task<PrepareOutcome> CapturePrepareAsync(SwitchRuntime runtime, Guid switchId)
        => await CapturePrepareAsync(runtime, runtime.Coordinator, switchId);

    private static async Task<PrepareOutcome> CapturePrepareAsync(
        SwitchRuntime runtime,
        ProfileSwitchCoordinator coordinator,
        Guid switchId)
    {
        try
        {
            var result = await coordinator.PrepareAsync(
                runtime.CreatePrepare(switchId),
                TestContext.Current.CancellationToken);
            return new PrepareOutcome(switchId, result.State, null);
        }
        catch (ProfileSelectorException ex)
        {
            return new PrepareOutcome(switchId, null, ex.ErrorCode);
        }
    }

    private sealed record PrepareOutcome(Guid SwitchId, ProfileSwitchState? State, string? ErrorCode);

    private sealed class TestCredentialReservation : IProfileSwitchCredentialReservation
    {
        private readonly Action<User>? _revalidateSessionPolicy;

        public TestCredentialReservation(Device credential, Action<User>? revalidateSessionPolicy = null)
        {
            Credential = credential;
            _revalidateSessionPolicy = revalidateSessionPolicy;
        }

        public Device Credential { get; }

        public void RevalidateSessionPolicy(User user)
        {
            _revalidateSessionPolicy?.Invoke(user);
        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow()
            => _utcNow;

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }

    private sealed class CommitFailureInterceptor : SaveChangesInterceptor
    {
        public bool FailCommittedOperation { get; set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (FailCommittedOperation
                && eventData.Context!.ChangeTracker.Entries<ProfileSelectorSwitchOperation>()
                    .Any(entry => entry.Entity.State is ProfileSelectorSwitchOperationState.Committed))
            {
                throw new DbUpdateException("Injected profile switch commit failure.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class SwitchRuntime : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly SaveChangesInterceptor? _saveChangesInterceptor;
        private readonly Mock<IDbContextFactory<JellyfinDbContext>> _dbContextFactory;
        private readonly Mock<IUserManager> _userManager;
        private readonly Mock<IDeviceManager> _deviceManager;
        private readonly Mock<INetworkManager> _networkManager;
        private readonly ICryptoProvider _cryptoProvider;
        private readonly ProfileSwitchOptions _options;
        private readonly List<SessionInfo> _sessions = [];
        private readonly object _devicesLock = new();
        private readonly object _sessionsLock = new();
        private bool _allowDevice = true;
        private bool _isLocalNetwork = true;

        private SwitchRuntime(
            string databasePath,
            SaveChangesInterceptor? saveChangesInterceptor,
            User owner,
            User target,
            List<Device> devices,
            ProfileSwitchOptions options)
        {
            _databasePath = databasePath;
            _saveChangesInterceptor = saveChangesInterceptor;
            Owner = owner;
            Target = target;
            Devices = devices;
            _options = options;
            Clock = new MutableTimeProvider();
            _dbContextFactory = new Mock<IDbContextFactory<JellyfinDbContext>>(MockBehavior.Strict);
            _dbContextFactory
                .Setup(factory => factory.CreateDbContext())
                .Returns(CreateDbContext);
            _dbContextFactory
                .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .Returns((CancellationToken _) => Task.FromResult(CreateDbContext()));

            _userManager = new Mock<IUserManager>(MockBehavior.Strict);
            _userManager.Setup(manager => manager.GetUserById(owner.Id)).Returns(owner);
            _userManager.Setup(manager => manager.GetUserById(target.Id)).Returns(target);
            _userManager
                .Setup(manager => manager.GetUserDto(It.IsAny<User>(), It.IsAny<string?>()))
                .Returns((User user, string? _) => new UserDto
                {
                    Id = user.Id,
                    Name = user.Username,
                    Policy = new UserPolicy { BlockedMediaFolders = [] }
                });

            _deviceManager = new Mock<IDeviceManager>(MockBehavior.Strict);
            _deviceManager
                .Setup(manager => manager.CanAccessDevice(It.Is<User>(user => user.Id.Equals(target.Id)), DeviceId))
                .Returns(() => _allowDevice);
            _deviceManager
                .Setup(manager => manager.CanAccessDevice(It.Is<User>(user => user.Id.Equals(owner.Id)), DeviceId))
                .Returns(() => _allowDevice);
            _deviceManager
                .Setup(manager => manager.GetDevices(It.IsAny<DeviceQuery>()))
                .Returns((DeviceQuery query) => QueryDevices(query));
            _deviceManager
                .Setup(manager => manager.ReconcileDevice(It.IsAny<int>()))
                .Returns(async (int authenticationDeviceId) =>
                {
                    await using var dbContext = CreateDbContext();
                    var device = await dbContext.Devices
                        .AsNoTracking()
                        .SingleOrDefaultAsync(
                            candidate => candidate.Id == authenticationDeviceId,
                            TestContext.Current.CancellationToken);
                    if (device is not null)
                    {
                        lock (_devicesLock)
                        {
                            if (Devices.All(candidate => candidate.Id != device.Id))
                            {
                                Devices.Add(device);
                            }
                        }
                    }

                    return device;
                });

            SessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
            SessionManager.SetupGet(manager => manager.Sessions).Returns(() =>
            {
                lock (_sessionsLock)
                {
                    return _sessions.ToList();
                }
            });
            SessionManager
                .Setup(manager => manager.ValidateProfileSwitchSessionPolicyAsync(It.IsAny<User>(), It.IsAny<string>()))
                .Returns((User user, string deviceId) =>
                {
                    ValidateSessionPolicy(user, deviceId);
                    return Task.CompletedTask;
                });
            SessionManager
                .Setup(manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), It.IsAny<Guid>()))
                .Returns((AuthenticationRequest request, Guid switchId) =>
                {
                    var device = new Device(request.UserId, request.App, request.AppVersion, request.DeviceName, request.DeviceId)
                    {
                        ProfileSwitchId = switchId
                    };
                    AfterCredentialCreated?.Invoke();
                    return Task.FromResult<IProfileSwitchCredentialReservation>(
                        new TestCredentialReservation(
                            device,
                            user => ValidateSessionPolicy(user, request.DeviceId)));
                });
            SessionManager
                .Setup(manager => manager.RegisterCommittedProfileSwitchCredential(It.IsAny<Device>()))
                .Callback((Device device) =>
                {
                    lock (_devicesLock)
                    {
                        if (Devices.All(candidate => candidate.Id != device.Id))
                        {
                            Devices.Add(device);
                        }
                    }
                });
            SessionManager
                .Setup(manager => manager.RevokeProfileSwitchCredential(It.IsAny<Device>()))
                .Returns((Device device) =>
                {
                    lock (_devicesLock)
                    {
                        Devices.RemoveAll(candidate => candidate.Id == device.Id);
                    }

                    _sessions.RemoveAll(session => session.UserId.Equals(device.UserId)
                                                   && string.Equals(session.DeviceId, device.DeviceId, StringComparison.Ordinal));
                    return Task.CompletedTask;
                });
            SessionManager
                .Setup(manager => manager.RevokeSupersededProfileCredential(It.IsAny<Device>(), It.IsAny<Guid>()))
                .Returns((Device device, Guid replacementSwitchId) =>
                {
                    if (FailSupersededCredentialCleanup)
                    {
                        throw new InvalidOperationException("Injected superseded credential cleanup failure.");
                    }

                    lock (_devicesLock)
                    {
                        var replacementExists = Devices.Any(
                            candidate => candidate.UserId.Equals(device.UserId)
                                         && string.Equals(candidate.DeviceId, device.DeviceId, StringComparison.Ordinal)
                                         && candidate.ProfileSwitchId.Equals(replacementSwitchId));
                        if (!replacementExists)
                        {
                            throw new InvalidOperationException(
                                "A replacement credential is required before revoking a superseded profile credential.");
                        }

                        Devices.RemoveAll(candidate => candidate.Id == device.Id);
                    }

                    return Task.CompletedTask;
                });
            SessionManager
                .Setup(manager => manager.GetSessionByAuthenticationToken(
                    It.IsAny<Device>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()))
                .Returns((Device device, string deviceId, string remoteEndPoint, string appVersion) =>
                {
                    lock (_sessionsLock)
                    {
                        var existingSession = _sessions.FirstOrDefault(
                            session => session.UserId.Equals(device.UserId)
                                       && string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal)
                                       && string.Equals(session.Client, device.AppName, StringComparison.Ordinal));
                        if (existingSession is not null)
                        {
                            return Task.FromResult(existingSession);
                        }

                        var session = new SessionInfo(SessionManager.Object, NullLogger.Instance)
                        {
                            Id = $"replayed-{device.AccessToken}",
                            UserId = device.UserId,
                            DeviceId = deviceId,
                            Client = device.AppName,
                            ApplicationVersion = appVersion,
                            RemoteEndPoint = remoteEndPoint
                        };
                        _sessions.Add(session);
                        return Task.FromResult(session);
                    }
                });
            SessionManager
                .Setup(manager => manager.ToSessionInfoDto(It.IsAny<SessionInfo>()))
                .Returns((SessionInfo session) => new SessionInfoDto
                {
                    Id = session.Id,
                    UserId = session.UserId,
                    DeviceId = session.DeviceId,
                    Client = session.Client
                });
            SessionManager
                .Setup(manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()))
                .Returns((ProfileSwitchSessionStopRequest request) => Task.FromResult(StopPlayback(request)));

            _networkManager = new Mock<INetworkManager>(MockBehavior.Strict);
            _networkManager.Setup(manager => manager.IsInLocalNetwork(RemoteEndPoint)).Returns(() => _isLocalNetwork);
            _cryptoProvider = new CryptographyProvider();
            TranscodeManager = new Mock<ITranscodeManager>(MockBehavior.Strict);
            TranscodeManager
                .Setup(manager => manager.KillTranscodingJobs(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<Func<string, bool>>()))
                .Returns(Task.CompletedTask);

            Coordinator = CreateCoordinator();
        }

        public User Owner { get; }

        public User Target { get; }

        public List<Device> Devices { get; }

        public MutableTimeProvider Clock { get; }

        public Mock<ISessionManager> SessionManager { get; }

        public Mock<ITranscodeManager> TranscodeManager { get; }

        public Action? AfterCredentialCreated { get; set; }

        public bool FailSupersededCredentialCleanup { get; set; }

        public ProfileSwitchCoordinator Coordinator { get; }

        public ProfileSwitchRequestContext RequestContext
            => new()
            {
                CurrentUserId = Owner.Id,
                CallerCredentialRecordId = 1001,
                DeviceId = DeviceId,
                DeviceName = "Profile switch tests",
                Client = "Integration tests",
                Version = "1.0.0",
                RemoteEndPoint = RemoteEndPoint
            };

        public static async Task<SwitchRuntime> CreateAsync(
            bool includeRecoveryCredential = false,
            SaveChangesInterceptor? saveChangesInterceptor = null,
            ProfileSwitchOptions? options = null)
        {
            var databasePath = Path.GetTempFileName();
            var owner = CreateUser("switch-owner", true);
            var target = CreateUser("switch-target", false);
            var devices = new List<Device>();
            if (includeRecoveryCredential)
            {
                devices.Add(new Device(owner.Id, "Integration tests", "1.0.0", "Recovery", DeviceId));
            }

            var runtime = new SwitchRuntime(
                databasePath,
                saveChangesInterceptor,
                owner,
                target,
                devices,
                options ?? new ProfileSwitchOptions());
            await using var dbContext = runtime.CreateDbContext();
            await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            dbContext.Users.AddRange(owner, target);
            var selector = new ProfileSelector(owner.Id);
            selector.Members.Add(new ProfileSelectorMember(selector.Id, owner.Id));
            selector.Members.Add(new ProfileSelectorMember(selector.Id, target.Id));
            dbContext.ProfileSelectors.Add(selector);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            return runtime;
        }

        public ProfileSwitchCoordinator CreateCoordinator()
            => new(
                new EfProfileSwitchStore(_dbContextFactory.Object),
                _userManager.Object,
                _deviceManager.Object,
                SessionManager.Object,
                _cryptoProvider,
                _networkManager.Object,
                TranscodeManager.Object,
                Clock,
                _options);

        private ProfileSwitchSessionStopResult StopPlayback(ProfileSwitchSessionStopRequest request)
        {
            var session = _sessions.FirstOrDefault(
                candidate => string.Equals(candidate.DeviceId, request.DeviceId, StringComparison.Ordinal)
                             && string.Equals(candidate.Client, request.Client, StringComparison.Ordinal));
            if (session is null || session.NowPlayingItem is null)
            {
                return new ProfileSwitchSessionStopResult
                {
                    Outcome = ProfileSwitchSessionStopOutcome.NotActive
                };
            }

            if (!session.UserId.Equals(request.UserId))
            {
                return new ProfileSwitchSessionStopResult
                {
                    Outcome = ProfileSwitchSessionStopOutcome.SessionMismatch
                };
            }

            if (!session.NowPlayingItem.Id.Equals(request.ItemId)
                || !string.Equals(session.PlaySessionId, request.PlaySessionId, StringComparison.Ordinal))
            {
                return new ProfileSwitchSessionStopResult
                {
                    Outcome = ProfileSwitchSessionStopOutcome.PlaybackMismatch
                };
            }

            session.NowPlayingItem = null;
            session.PlaySessionId = null;
            return new ProfileSwitchSessionStopResult
            {
                Outcome = ProfileSwitchSessionStopOutcome.Stopped,
                PlaySessionId = request.PlaySessionId
            };
        }

        private void ValidateSessionPolicy(User user, string deviceId)
        {
            var activeSessions = _sessions.Count(
                session => session.UserId.Equals(user.Id)
                           && !string.Equals(session.DeviceId, deviceId, StringComparison.Ordinal));
            if (user.MaxActiveSessions >= 1 && activeSessions >= user.MaxActiveSessions)
            {
                throw new MediaBrowser.Controller.Net.SecurityException("User is at their maximum number of sessions.");
            }
        }

        public (ProfileSwitchCoordinator Coordinator, Mock<ISessionManager> SessionManager) CreatePersistedDeviceCoordinator()
        {
            var deviceManager = new DeviceManager(_dbContextFactory.Object, _userManager.Object);
            var sessions = new List<SessionInfo>();
            var sessionManager = new Mock<ISessionManager>(MockBehavior.Strict);
            sessionManager.SetupGet(manager => manager.Sessions).Returns(sessions);
            sessionManager
                .Setup(manager => manager.ValidateProfileSwitchSessionPolicyAsync(It.IsAny<User>(), It.IsAny<string>()))
                .Returns(Task.CompletedTask);
            sessionManager
                .Setup(manager => manager.CreateProfileSwitchCredential(It.IsAny<AuthenticationRequest>(), It.IsAny<Guid>()))
                .Returns((AuthenticationRequest request, Guid switchId) =>
                    Task.FromResult<IProfileSwitchCredentialReservation>(
                        new TestCredentialReservation(
                            new Device(request.UserId, request.App, request.AppVersion, request.DeviceName, request.DeviceId)
                            {
                                ProfileSwitchId = switchId
                            })));
            sessionManager
                .Setup(manager => manager.RegisterCommittedProfileSwitchCredential(It.IsAny<Device>()))
                .Callback((Device device) => deviceManager.RegisterDevice(device));
            sessionManager
                .Setup(manager => manager.RevokeProfileSwitchCredential(It.IsAny<Device>()))
                .Returns((Device device) => deviceManager.DeleteDevice(device));
            sessionManager
                .Setup(manager => manager.RevokeSupersededProfileCredential(It.IsAny<Device>(), It.IsAny<Guid>()))
                .Returns((Device device, Guid _) => deviceManager.DeleteDevice(device));
            sessionManager
                .Setup(manager => manager.GetSessionByAuthenticationToken(
                    It.IsAny<Device>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<string>()))
                .Returns((Device device, string deviceId, string remoteEndPoint, string appVersion) =>
                {
                    var session = new SessionInfo(sessionManager.Object, NullLogger.Instance)
                    {
                        Id = $"persisted-{device.AccessToken}",
                        UserId = device.UserId,
                        DeviceId = deviceId,
                        Client = device.AppName,
                        ApplicationVersion = appVersion,
                        RemoteEndPoint = remoteEndPoint
                    };
                    sessions.Add(session);
                    return Task.FromResult(session);
                });
            sessionManager
                .Setup(manager => manager.ToSessionInfoDto(It.IsAny<SessionInfo>()))
                .Returns((SessionInfo session) => new SessionInfoDto
                {
                    Id = session.Id,
                    UserId = session.UserId,
                    DeviceId = session.DeviceId,
                    Client = session.Client
                });
            sessionManager
                .Setup(manager => manager.StopProfileSwitchPlaybackAsync(It.IsAny<ProfileSwitchSessionStopRequest>()))
                .ReturnsAsync(new ProfileSwitchSessionStopResult
                {
                    Outcome = ProfileSwitchSessionStopOutcome.NotActive
                });

            var coordinator = new ProfileSwitchCoordinator(
                new EfProfileSwitchStore(_dbContextFactory.Object),
                _userManager.Object,
                deviceManager,
                sessionManager.Object,
                _cryptoProvider,
                _networkManager.Object,
                TranscodeManager.Object,
                Clock,
                _options);
            return (coordinator, sessionManager);
        }

        public ProfileSwitchPrepareContext CreatePrepare(Guid switchId)
            => new()
            {
                SwitchId = switchId,
                TargetProfileUserId = Target.Id,
                RequestContext = RequestContext
            };

        public ProfileSwitchRequestContext CreateRequestContext(Guid callerUserId)
        {
            var context = RequestContext;
            context.CurrentUserId = callerUserId;
            context.CallerCredentialRecordId = callerUserId.Equals(Owner.Id) ? 1001 : 1002;
            return context;
        }

        public PlaybackStopInfo StartOldPlayback()
            => StartPlaybackFor(Owner.Id, DeviceId);

        public PlaybackStopInfo StartPlaybackFor(Guid userId, string deviceId)
        {
            var info = CreatePlaybackStop();
            _sessions.Clear();
            var session = new SessionInfo(SessionManager.Object, NullLogger.Instance)
            {
                Id = info.SessionId,
                UserId = userId,
                DeviceId = deviceId,
                Client = "Integration tests",
                NowPlayingItem = new BaseItemDto { Id = info.ItemId },
                PlaySessionId = info.PlaySessionId,
                PlaylistItemId = "server-playlist-item",
                NowPlayingQueue = [new QueueItem { Id = info.ItemId, PlaylistItemId = "server-queue-item" }]
            };
            session.PlayState.MediaSourceId = "server-media-source";
            session.PlayState.LiveStreamId = "server-live-stream";
            _sessions.Add(session);
            return info;
        }

        public PlaybackStopInfo CreatePlaybackStop()
            => new()
            {
                SessionId = "captured-old-session",
                ItemId = Guid.Parse("8418b479-0dba-45bf-996a-0d25950cbbe4"),
                MediaSourceId = "source",
                PositionTicks = 420000000,
                PlaySessionId = "captured-play-session"
            };

        public void AddSession(Guid userId, string deviceId)
        {
            _sessions.Add(new SessionInfo(SessionManager.Object, NullLogger.Instance)
            {
                Id = $"session-{userId:N}-{deviceId}",
                UserId = userId,
                DeviceId = deviceId,
                Client = "Integration tests"
            });
        }

        public async Task<Device> AddTargetCredentialAsync()
        {
            var credential = new Device(Target.Id, "Integration tests", "1.0.0", "Superseded", DeviceId);
            await using var dbContext = CreateDbContext();
            dbContext.Devices.Add(credential);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
            lock (_devicesLock)
            {
                Devices.Add(credential);
            }

            return credential;
        }

        public void RemoveCachedCredential(int authenticationDeviceId)
        {
            lock (_devicesLock)
            {
                Devices.RemoveAll(device => device.Id == authenticationDeviceId);
            }
        }

        public async Task ApplyCommitDenialAsync(MutableCommitDenial denial)
        {
            if (denial is MutableCommitDenial.Disabled)
            {
                Target.SetPermission(PermissionKind.IsDisabled, true);
                return;
            }

            if (denial is MutableCommitDenial.Remote)
            {
                Target.SetPermission(PermissionKind.EnableRemoteAccess, false);
                _isLocalNetwork = false;
                return;
            }

            if (denial is MutableCommitDenial.Schedule)
            {
                Target.AccessSchedules.Add(new AccessSchedule(DynamicDayOfWeek.Everyday, 25, 26, Target.Id));
                return;
            }

            if (denial is MutableCommitDenial.Device)
            {
                _allowDevice = false;
                return;
            }

            if (denial is MutableCommitDenial.MaxSessions)
            {
                Target.MaxActiveSessions = 1;
                _sessions.Add(new SessionInfo(SessionManager.Object, NullLogger.Instance)
                {
                    Id = "existing-target-session",
                    UserId = Target.Id,
                    DeviceId = "other-device"
                });
                return;
            }

            await using var dbContext = CreateDbContext();
            if (denial is MutableCommitDenial.SelectorDisabled)
            {
                var selector = await dbContext.ProfileSelectors.SingleAsync(TestContext.Current.CancellationToken);
                selector.IsEnabled = false;
                await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
                return;
            }

            var member = await dbContext.ProfileSelectorMembers.SingleAsync(
                candidate => candidate.ProfileUserId.Equals(Target.Id),
                TestContext.Current.CancellationToken);
            if (denial is MutableCommitDenial.Hidden)
            {
                member.IsVisible = false;
            }
            else if (denial is MutableCommitDenial.Locked)
            {
                member.PinLockoutUntilUtc = Clock.GetUtcNow().UtcDateTime.AddMinutes(1);
            }
            else if (denial is MutableCommitDenial.Removed)
            {
                dbContext.ProfileSelectorMembers.Remove(member);
            }

            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task SetTargetMaxActiveSessionsAsync(int maxActiveSessions)
        {
            Target.MaxActiveSessions = maxActiveSessions;
            await using var dbContext = CreateDbContext();
            var target = await dbContext.Users.SingleAsync(
                user => user.Id.Equals(Target.Id),
                TestContext.Current.CancellationToken);
            target.MaxActiveSessions = maxActiveSessions;
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public void DisableTargetDurably()
        {
            Target.SetPermission(PermissionKind.IsDisabled, true);
            using var dbContext = CreateDbContext();
            var target = dbContext.Users
                .Include(user => user.Permissions)
                .Single(user => user.Id.Equals(Target.Id));
            target.SetPermission(PermissionKind.IsDisabled, true);
            dbContext.SaveChanges();
        }

        public async Task SetTargetPinAsync(string pin)
        {
            await using var dbContext = CreateDbContext();
            var member = await dbContext.ProfileSelectorMembers.SingleAsync(
                candidate => candidate.ProfileUserId.Equals(Target.Id),
                TestContext.Current.CancellationToken);
            member.PinHash = _cryptoProvider.CreatePasswordHash(pin).ToString();
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public async Task RemoveTargetMembershipAsync()
        {
            await using var dbContext = CreateDbContext();
            var member = await dbContext.ProfileSelectorMembers.SingleAsync(
                candidate => candidate.ProfileUserId.Equals(Target.Id),
                TestContext.Current.CancellationToken);
            dbContext.ProfileSelectorMembers.Remove(member);
            await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        public JellyfinDbContext CreateDbContext()
        {
            var optionsBuilder = new DbContextOptionsBuilder<JellyfinDbContext>()
                .UseSqlite($"Data Source={_databasePath}");
            if (_saveChangesInterceptor is not null)
            {
                optionsBuilder.AddInterceptors(_saveChangesInterceptor);
            }

            return new JellyfinDbContext(
                optionsBuilder.Options,
                NullLogger<JellyfinDbContext>.Instance,
                new SqliteDatabaseProvider(null!, NullLogger<SqliteDatabaseProvider>.Instance),
                new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
        }

        public ValueTask DisposeAsync()
        {
            Coordinator.Dispose();
            File.Delete(_databasePath);
            return ValueTask.CompletedTask;
        }

        private QueryResult<Device> QueryDevices(DeviceQuery query)
        {
            lock (_devicesLock)
            {
                var matches = Devices
                    .Where(device => !query.UserId.HasValue || device.UserId.Equals(query.UserId.Value))
                    .Where(device => query.DeviceId is null || device.DeviceId == query.DeviceId)
                    .Where(device => query.AccessToken is null || device.AccessToken == query.AccessToken)
                    .Where(device => !query.ProfileSwitchId.HasValue || device.ProfileSwitchId.Equals(query.ProfileSwitchId.Value))
                    .ToList();
                var count = matches.Count;
                if (query.Skip.HasValue)
                {
                    matches = matches.Skip(query.Skip.Value).ToList();
                }

                if (query.Limit.HasValue && query.Limit.Value > 0)
                {
                    matches = matches.Take(query.Limit.Value).ToList();
                }

                return new QueryResult<Device>(query.Skip, count, matches);
            }
        }

        private static User CreateUser(string username, bool isAdministrator)
        {
            var user = new User(username, "test-auth-provider", "test-password-reset-provider");
            user.AddDefaultPermissions();
            user.AddDefaultPreferences();
            user.SetPermission(PermissionKind.IsAdministrator, isAdministrator);
            return user;
        }
    }
}
