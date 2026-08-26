using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Session;

namespace Jellyfin.Server.Implementations.Users
{
    /// <summary>
    /// Coordinates the durable server half of profile switching and old-session playback quiescence.
    /// </summary>
    public sealed class ProfileSwitchCoordinator : IProfileSwitchCoordinator, IDisposable
    {
        private const int MinPinLength = 4;
        private const int MaxPinLength = 8;
        private const int PinFailureBackoffThreshold = 3;

        private readonly IProfileSwitchStore _profileSwitchStore;
        private readonly IUserManager _userManager;
        private readonly IDeviceManager _deviceManager;
        private readonly ISessionManager _sessionManager;
        private readonly ICryptoProvider _cryptoProvider;
        private readonly INetworkManager _networkManager;
        private readonly ITranscodeManager _transcodeManager;
        private readonly TimeProvider _timeProvider;
        private readonly ProfileSwitchOptions _options;
        private readonly SemaphoreSlim _operationLock = new(1, 1);

        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSwitchCoordinator"/> class.
        /// </summary>
        public ProfileSwitchCoordinator(
            IProfileSwitchStore profileSwitchStore,
            IUserManager userManager,
            IDeviceManager deviceManager,
            ISessionManager sessionManager,
            ICryptoProvider cryptoProvider,
            INetworkManager networkManager,
            ITranscodeManager transcodeManager,
            TimeProvider timeProvider,
            ProfileSwitchOptions options)
        {
            _profileSwitchStore = profileSwitchStore;
            _userManager = userManager;
            _deviceManager = deviceManager;
            _sessionManager = sessionManager;
            _cryptoProvider = cryptoProvider;
            _networkManager = networkManager;
            _transcodeManager = transcodeManager;
            _timeProvider = timeProvider;
            _options = options;
            _options.Validate();
        }

        /// <inheritdoc />
        public Task<ProfileSwitchResult> PrepareAsync(ProfileSwitchPrepareContext context, CancellationToken cancellationToken)
        {
            ValidatePrepareContext(context);
            return ExecuteLockedAsync(() => PrepareCoreAsync(context, cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public Task<ProfileSwitchResult> CommitAsync(Guid switchId, ProfileSwitchRequestContext requestContext, CancellationToken cancellationToken)
        {
            ValidateSwitchId(switchId);
            ValidateRequestContext(requestContext);
            return ExecuteLockedAsync(() => CommitCoreAsync(switchId, requestContext, cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public Task<ProfileSwitchResult> GetStatusAsync(Guid switchId, ProfileSwitchRequestContext requestContext, CancellationToken cancellationToken)
        {
            ValidateSwitchId(switchId);
            ValidateRequestContext(requestContext);
            return ExecuteLockedAsync(() => GetStatusCoreAsync(switchId, requestContext, cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public Task<ProfileSwitchResult> AbortAsync(Guid switchId, ProfileSwitchRequestContext requestContext, CancellationToken cancellationToken)
        {
            ValidateSwitchId(switchId);
            ValidateRequestContext(requestContext);
            return ExecuteLockedAsync(() => AbortCoreAsync(switchId, requestContext, cancellationToken), cancellationToken);
        }

        /// <inheritdoc />
        public Task<ProfileSwitchPlaybackStopResult> ReportPlaybackStoppedAsync(
            Guid switchId,
            ProfileSwitchRequestContext requestContext,
            PlaybackStopInfo playbackStopInfo,
            CancellationToken cancellationToken)
        {
            ValidateSwitchId(switchId);
            ValidateRequestContext(requestContext);
            ValidatePlaybackStop(playbackStopInfo);
            return ExecuteLockedAsync(
                () => ReportPlaybackStoppedCoreAsync(switchId, requestContext, playbackStopInfo, cancellationToken),
                cancellationToken);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _operationLock.Dispose();
        }

        private async Task<ProfileSwitchResult> PrepareCoreAsync(ProfileSwitchPrepareContext context, CancellationToken cancellationToken)
        {
            var requestContext = context.RequestContext!;
            var now = GetUtcNow();
            await RunMaintenanceAsync(now, cancellationToken).ConfigureAwait(false);
            var dbContext = await _profileSwitchStore.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);

            var existingOperation = await dbContext.FindOperationAsync(context.SwitchId, cancellationToken).ConfigureAwait(false);
            if (existingOperation is not null)
            {
                EnsureBoundRequest(existingOperation, requestContext);
                EnsurePrepareReplayMatches(existingOperation, context);
                await ExpireIfNeededAsync(dbContext, existingOperation, now, cancellationToken).ConfigureAwait(false);
                await EnsureRetainedAsync(dbContext, existingOperation, now, cancellationToken).ConfigureAwait(false);
                return await BuildResultAsync(existingOperation, requestContext.RemoteEndPoint!, cancellationToken).ConfigureAwait(false);
            }

            await EnsureOperationQuotaAsync(dbContext, requestContext, now, cancellationToken).ConfigureAwait(false);

            var selector = await LoadSelectorForCallerAsync(dbContext, requestContext.CurrentUserId, cancellationToken).ConfigureAwait(false);
            EnsureSelectorEnabled(selector);
            var targetMember = EnsureTargetMember(selector!, context.TargetProfileUserId);
            EnsureNotRedundantMemberSwitch(selector!, requestContext.CurrentUserId, context.TargetProfileUserId);
            var targetUser = GetTargetUser(context.TargetProfileUserId);

            await ValidatePreparedPinAsync(dbContext, targetMember, context.Pin, now, cancellationToken).ConfigureAwait(false);
            EnsureTargetEligible(targetUser, requestContext.DeviceId!, requestContext.RemoteEndPoint!, now);
            await EnsureTargetSessionPolicyAsync(targetUser, requestContext.DeviceId!).ConfigureAwait(false);

            var activeOperation = await dbContext.FindActiveOperationAsync(
                selector!.Id,
                requestContext.DeviceId!,
                cancellationToken).ConfigureAwait(false);
            if (activeOperation is not null)
            {
                await ExpireIfNeededAsync(dbContext, activeOperation, now, cancellationToken).ConfigureAwait(false);
                if (activeOperation.State is ProfileSelectorSwitchOperationState.Prepared)
                {
                    throw Conflict(
                        "PROFILE_SWITCH_ALREADY_IN_PROGRESS",
                        "Another profile switch is already prepared for this selector and device.");
                }
            }

            var operation = new ProfileSelectorSwitchOperation(
                context.SwitchId,
                selector!.Id,
                selector.OwnerUserId,
                requestContext.CurrentUserId,
                requestContext.CallerCredentialRecordId,
                context.TargetProfileUserId,
                requestContext.DeviceId!,
                requestContext.DeviceName!,
                requestContext.Client!,
                requestContext.Version!,
                requestContext.RemoteEndPoint!,
                now,
                now.Add(_options.PreparedLifetime),
                now.Add(_options.PreparedLifetime).Add(_options.TerminalRetention));
            if (!string.IsNullOrEmpty(context.Pin))
            {
                operation.PinProofHash = _cryptoProvider.CreatePasswordHash(context.Pin).ToString();
            }

            dbContext.AddOperation(operation);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ProfileSwitchPersistenceConflictException)
            {
                var racedResult = await ResolvePrepareConstraintAsync(context, selector.Id, now, cancellationToken).ConfigureAwait(false);
                if (racedResult is not null)
                {
                    return racedResult;
                }

                throw;
            }

            return MapResult(operation);
        }

        private async Task<ProfileSwitchResult?> ResolvePrepareConstraintAsync(
            ProfileSwitchPrepareContext context,
            Guid selectorId,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var requestContext = context.RequestContext!;
            var dbContext = await _profileSwitchStore.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var sameSwitch = await dbContext.FindOperationAsync(context.SwitchId, cancellationToken).ConfigureAwait(false);
            if (sameSwitch is not null)
            {
                EnsureBoundRequest(sameSwitch, requestContext);
                EnsurePrepareReplayMatches(sameSwitch, context);
                await ExpireIfNeededAsync(dbContext, sameSwitch, now, cancellationToken).ConfigureAwait(false);
                await EnsureRetainedAsync(dbContext, sameSwitch, now, cancellationToken).ConfigureAwait(false);
                return await BuildResultAsync(sameSwitch, requestContext.RemoteEndPoint!, cancellationToken).ConfigureAwait(false);
            }

            var competingSwitch = await dbContext.FindActiveOperationAsync(
                selectorId,
                requestContext.DeviceId!,
                cancellationToken).ConfigureAwait(false);
            if (competingSwitch is not null)
            {
                throw Conflict(
                    "PROFILE_SWITCH_ALREADY_IN_PROGRESS",
                    "Another profile switch is already prepared for this selector and device.");
            }

            return null;
        }

        private async Task<ProfileSwitchResult> CommitCoreAsync(
            Guid switchId,
            ProfileSwitchRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            var now = GetUtcNow();
            await RunMaintenanceAsync(now, cancellationToken).ConfigureAwait(false);
            var dbContext = await _profileSwitchStore.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var operation = await LoadBoundOperationAsync(dbContext, switchId, requestContext, cancellationToken).ConfigureAwait(false);
            await ExpireIfNeededAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);
            await EnsureRetainedAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);

            if (operation.State is ProfileSelectorSwitchOperationState.Committed)
            {
                return await BuildResultAsync(operation, requestContext.RemoteEndPoint!, cancellationToken).ConfigureAwait(false);
            }

            EnsurePrepared(operation);

            try
            {
                var preliminarySelector = await LoadSelectorByIdAsync(dbContext, operation.ProfileSelectorId, cancellationToken).ConfigureAwait(false);
                EnsureSelectorEnabled(preliminarySelector);
                EnsureCallerAuthority(preliminarySelector, operation.CallerUserId);
                var preliminaryTargetMember = EnsureTargetMember(preliminarySelector, operation.TargetProfileUserId);
                EnsureTargetNotLocked(preliminaryTargetMember, now);
                var preliminaryTargetUser = GetTargetUser(operation.TargetProfileUserId);
                EnsureTargetEligible(preliminaryTargetUser, operation.DeviceId, requestContext.RemoteEndPoint!, now);
                await EnsureTargetSessionPolicyAsync(preliminaryTargetUser, operation.DeviceId).ConfigureAwait(false);
                await EnsurePlaybackSettledAsync(dbContext, operation.SwitchId, cancellationToken).ConfigureAwait(false);
            }
            catch (ProfileSelectorException)
            {
                await AbortBeforeCommitAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);
                throw;
            }

            await RemovePreparedOrphanCredentialsAsync(operation.SwitchId).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IProfileSwitchCredentialReservation credentialReservation;
            try
            {
                credentialReservation = await _sessionManager.CreateProfileSwitchCredential(
                    CreateAuthenticationRequest(operation, requestContext.RemoteEndPoint!),
                    operation.SwitchId).ConfigureAwait(false);
            }
            catch (AuthenticationException)
            {
                await AbortBeforeCommitAsync(dbContext, operation, now, CancellationToken.None).ConfigureAwait(false);
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Forbidden,
                    "PROFILE_SWITCH_AUTHENTICATION_FAILED",
                    "The selected profile could not be authenticated.");
            }
            catch (SecurityException ex)
            {
                await AbortBeforeCommitAsync(dbContext, operation, now, CancellationToken.None).ConfigureAwait(false);
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_SWITCH_AUTHENTICATION_FORBIDDEN", ex.Message);
            }

            await using var configuredCredentialReservation = credentialReservation.ConfigureAwait(false);
            var authenticationDevice = credentialReservation.Credential;

            if (!authenticationDevice.ProfileSwitchId.Equals(operation.SwitchId)
                || !authenticationDevice.UserId.Equals(operation.TargetProfileUserId)
                || !string.Equals(authenticationDevice.DeviceId, operation.DeviceId, StringComparison.Ordinal)
                || authenticationDevice.Id != 0)
            {
                await RevokeProfileSwitchCredentialAsync(authenticationDevice).ConfigureAwait(false);
                throw new InvalidOperationException("The profile switch credential adapter returned an invalid or already-persisted credential.");
            }

            var commitContext = await _profileSwitchStore.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using var configuredCommitContext = commitContext.ConfigureAwait(false);
            await using var transaction = await commitContext.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);
            var commitNow = GetUtcNow();
            var commitOperation = await LoadBoundOperationAsync(
                commitContext,
                switchId,
                requestContext,
                CancellationToken.None).ConfigureAwait(false);

            if (commitOperation.State is ProfileSelectorSwitchOperationState.Committed)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                return await BuildResultAsync(commitOperation, requestContext.RemoteEndPoint!, CancellationToken.None).ConfigureAwait(false);
            }

            await ExpireIfNeededAsync(commitContext, commitOperation, commitNow, CancellationToken.None).ConfigureAwait(false);
            if (commitOperation.State is ProfileSelectorSwitchOperationState.Expired)
            {
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                EnsurePrepared(commitOperation);
            }

            EnsurePrepared(commitOperation);

            ProfileSelector selector;
            ProfileSelectorMember targetMember;
            User targetUser;
            try
            {
                selector = await LoadSelectorByIdAsync(
                    commitContext,
                    commitOperation.ProfileSelectorId,
                    CancellationToken.None).ConfigureAwait(false);
                EnsureSelectorEnabled(selector);
                EnsureCallerAuthority(selector, commitOperation.CallerUserId);
                targetMember = EnsureTargetMember(selector, commitOperation.TargetProfileUserId);
                EnsureTargetNotLocked(targetMember, commitNow);
                targetUser = await LoadTargetUserPolicyAsync(
                    commitContext,
                    commitOperation.TargetProfileUserId,
                    CancellationToken.None).ConfigureAwait(false);
                EnsureTargetEligible(targetUser, commitOperation.DeviceId, requestContext.RemoteEndPoint!, commitNow);
                RevalidateReservedSessionPolicy(credentialReservation, targetUser);
                await EnsurePlaybackSettledAsync(
                    commitContext,
                    commitOperation.SwitchId,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (ProfileSelectorException)
            {
                await AbortBeforeCommitAsync(
                    commitContext,
                    commitOperation,
                    commitNow,
                    CancellationToken.None).ConfigureAwait(false);
                await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            commitContext.AssertUserPolicyUnchanged(targetUser);
            commitContext.AddAuthenticationDevice(authenticationDevice);
            await commitContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

            targetMember.FailedPinAttemptCount = 0;
            targetMember.PinLockoutUntilUtc = null;
            targetMember.LastFailedPinAttemptUtc = null;
            selector.DateModified = commitNow;

            var deviceState = selector.DeviceStates.FirstOrDefault(
                state => string.Equals(state.DeviceId, commitOperation.DeviceId, StringComparison.Ordinal));
            if (deviceState is null)
            {
                deviceState = new ProfileSelectorDeviceState(selector.Id, commitOperation.DeviceId);
                selector.DeviceStates.Add(deviceState);
            }

            deviceState.ActiveProfileUserId = commitOperation.TargetProfileUserId;
            deviceState.LastActivatedUtc = commitNow;
            commitOperation.State = ProfileSelectorSwitchOperationState.Committed;
            commitOperation.ActiveDeviceId = null;
            commitOperation.AuthenticationDeviceRecordId = authenticationDevice.Id;
            commitOperation.DateModifiedUtc = commitNow;
            commitOperation.RetainUntilUtc = commitNow.Add(_options.TerminalRetention);

            await commitContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
            await transaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _sessionManager.RegisterCommittedProfileSwitchCredential(authenticationDevice);
            }
            catch
            {
                await ReconcileAuthenticationDeviceAsync(commitOperation).ConfigureAwait(false);
                throw;
            }

            return await BuildResultAsync(commitOperation, requestContext.RemoteEndPoint!, CancellationToken.None).ConfigureAwait(false);
        }

        private async Task<ProfileSwitchResult> GetStatusCoreAsync(
            Guid switchId,
            ProfileSwitchRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            await RunMaintenanceAsync(GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var dbContext = await _profileSwitchStore.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var operation = await LoadBoundOperationAsync(dbContext, switchId, requestContext, cancellationToken).ConfigureAwait(false);
            var now = GetUtcNow();
            await ExpireIfNeededAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);
            await EnsureRetainedAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);
            return await BuildResultAsync(operation, requestContext.RemoteEndPoint!, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ProfileSwitchResult> AbortCoreAsync(
            Guid switchId,
            ProfileSwitchRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            await RunMaintenanceAsync(GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var dbContext = await _profileSwitchStore.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var operation = await LoadBoundOperationAsync(dbContext, switchId, requestContext, cancellationToken).ConfigureAwait(false);
            var now = GetUtcNow();
            await ExpireIfNeededAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);
            await EnsureRetainedAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);

            if (operation.State is ProfileSelectorSwitchOperationState.Committed)
            {
                throw Conflict("PROFILE_SWITCH_ALREADY_COMMITTED", "A committed profile switch cannot be aborted.");
            }

            if (operation.State is ProfileSelectorSwitchOperationState.Prepared)
            {
                await AbortBeforeCommitAsync(dbContext, operation, now, cancellationToken).ConfigureAwait(false);
            }

            return MapResult(operation);
        }

        private async Task<ProfileSwitchPlaybackStopResult> ReportPlaybackStoppedCoreAsync(
            Guid switchId,
            ProfileSwitchRequestContext requestContext,
            PlaybackStopInfo playbackStopInfo,
            CancellationToken cancellationToken)
        {
            await RunMaintenanceAsync(GetUtcNow(), cancellationToken).ConfigureAwait(false);
            var dbContext = await _profileSwitchStore.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            ProfileSelectorSwitchOperation operation;
            ProfileSelectorPlaybackStopReport receipt;
            await using (var reservationTransaction = await dbContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false))
            {
                operation = await LoadBoundOperationAsync(dbContext, switchId, requestContext, cancellationToken).ConfigureAwait(false);
                await ExpireIfNeededAsync(dbContext, operation, GetUtcNow(), cancellationToken).ConfigureAwait(false);
                EnsurePrepared(operation);

                var reportKey = CreatePlaybackReportKey(switchId, playbackStopInfo.PlaySessionId);
                var requestHash = CreatePlaybackRequestHash(playbackStopInfo);
                var existingReceipt = await dbContext.FindPlaybackReportAsync(reportKey, cancellationToken).ConfigureAwait(false);
                if (existingReceipt is not null)
                {
                    EnsurePlaybackReplayMatches(existingReceipt, operation, requestHash);
                    await reservationTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return MapPlaybackResult(existingReceipt);
                }

                var reportCount = await dbContext.CountPlaybackReportsAsync(switchId, cancellationToken).ConfigureAwait(false);
                if (reportCount >= _options.MaxPlaybackReportsPerSwitch)
                {
                    throw TooManyRequests(
                        "PROFILE_SWITCH_PLAYBACK_REPORT_LIMIT",
                        "The playback receipt quota for this profile switch has been reached.");
                }

                var now = GetUtcNow();
                receipt = new ProfileSelectorPlaybackStopReport(
                    reportKey,
                    switchId,
                    operation.CallerUserId,
                    operation.DeviceId,
                    playbackStopInfo.PlaySessionId,
                    requestHash,
                    now);
                dbContext.AddPlaybackReport(receipt);
                await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                await reservationTransaction.CommitAsync(CancellationToken.None).ConfigureAwait(false);
            }

            var sessionStopResult = await _sessionManager.StopProfileSwitchPlaybackAsync(
                new ProfileSwitchSessionStopRequest
                {
                    UserId = operation.CallerUserId,
                    DeviceId = operation.DeviceId,
                    Client = operation.Client,
                    ItemId = playbackStopInfo.ItemId,
                    PlaySessionId = playbackStopInfo.PlaySessionId,
                    PositionTicks = playbackStopInfo.PositionTicks!.Value,
                    Failed = playbackStopInfo.Failed,
                    NextMediaType = playbackStopInfo.NextMediaType
                }).ConfigureAwait(false);
            if (sessionStopResult.Outcome is ProfileSwitchSessionStopOutcome.NotActive)
            {
                receipt.State = ProfileSelectorPlaybackStopReportState.NotActive;
                receipt.DateModifiedUtc = GetUtcNow();
                await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                return MapPlaybackResult(receipt);
            }

            if (sessionStopResult.Outcome is ProfileSwitchSessionStopOutcome.SessionMismatch)
            {
                await FailPlaybackReceiptAsync(dbContext, receipt).ConfigureAwait(false);
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Forbidden,
                    "PROFILE_SWITCH_PLAYBACK_SESSION_MISMATCH",
                    "The captured playback session does not belong to the prepared old identity.");
            }

            if (sessionStopResult.Outcome is ProfileSwitchSessionStopOutcome.PlaybackMismatch)
            {
                await FailPlaybackReceiptAsync(dbContext, receipt).ConfigureAwait(false);
                throw Conflict(
                    "PROFILE_SWITCH_PLAYBACK_SNAPSHOT_MISMATCH",
                    "The playback report does not match the server-observed old playback session.");
            }

            var acknowledged = false;
            try
            {
                await _transcodeManager.KillTranscodingJobs(
                    operation.DeviceId,
                    sessionStopResult.PlaySessionId,
                    _ => true).ConfigureAwait(false);
                receipt.State = ProfileSelectorPlaybackStopReportState.Acknowledged;
                receipt.DateModifiedUtc = GetUtcNow();
                await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
                acknowledged = true;
                return MapPlaybackResult(receipt);
            }
            finally
            {
                if (!acknowledged)
                {
                    await FailPlaybackReceiptAsync(dbContext, receipt).ConfigureAwait(false);
                }
            }
        }

        private async Task<ProfileSwitchResult> BuildResultAsync(
            ProfileSelectorSwitchOperation operation,
            string remoteEndPoint,
            CancellationToken cancellationToken)
        {
            if (operation.State is not ProfileSelectorSwitchOperationState.Committed)
            {
                return MapResult(operation);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var authenticationDevice = await ReconcileAuthenticationDeviceAsync(operation).ConfigureAwait(false);
            await RemoveSupersededTargetRuntimeCredentialsAsync(
                authenticationDevice,
                operation.OwnerUserId.Equals(operation.TargetProfileUserId)).ConfigureAwait(false);
            var authenticationResult = await RestoreAuthenticationAsync(
                operation,
                authenticationDevice,
                remoteEndPoint,
                cancellationToken).ConfigureAwait(false);
            return MapResult(operation, authenticationResult);
        }

        private async Task<AuthenticationResult> RestoreAuthenticationAsync(
            ProfileSelectorSwitchOperation operation,
            Device authenticationDevice,
            string remoteEndPoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetUser = _userManager.GetUserById(operation.TargetProfileUserId);
            if (targetUser is null)
            {
                throw Conflict(
                    "PROFILE_SWITCH_AUTHENTICATION_UNAVAILABLE",
                    "The committed profile is no longer available; authenticate again.");
            }

            var session = _sessionManager.Sessions.FirstOrDefault(
                candidate => candidate.UserId.Equals(operation.TargetProfileUserId)
                             && string.Equals(candidate.DeviceId, operation.DeviceId, StringComparison.Ordinal)
                             && string.Equals(candidate.Client, operation.Client, StringComparison.Ordinal));
            session ??= await _sessionManager.GetSessionByAuthenticationToken(
                authenticationDevice,
                operation.DeviceId,
                remoteEndPoint,
                operation.Version).ConfigureAwait(false);

            return new AuthenticationResult
            {
                User = _userManager.GetUserDto(targetUser, remoteEndPoint),
                SessionInfo = _sessionManager.ToSessionInfoDto(session),
                AccessToken = authenticationDevice.AccessToken,
                ServerId = session.ServerId
            };
        }

        private async Task<Device> ReconcileAuthenticationDeviceAsync(ProfileSelectorSwitchOperation operation)
        {
            if (!operation.AuthenticationDeviceRecordId.HasValue)
            {
                throw Conflict(
                    "PROFILE_SWITCH_AUTHENTICATION_UNAVAILABLE",
                    "The committed profile credential is no longer available; authenticate again.");
            }

            var device = await _deviceManager.ReconcileDevice(operation.AuthenticationDeviceRecordId.Value).ConfigureAwait(false);
            if (device is null
                || device.Id != operation.AuthenticationDeviceRecordId.Value
                || !device.UserId.Equals(operation.TargetProfileUserId)
                || !device.ProfileSwitchId.Equals(operation.SwitchId)
                || !string.Equals(device.DeviceId, operation.DeviceId, StringComparison.Ordinal))
            {
                throw Conflict(
                    "PROFILE_SWITCH_AUTHENTICATION_UNAVAILABLE",
                    "The committed profile credential is no longer available; authenticate again.");
            }

            return device;
        }

        private async Task<ProfileSelectorSwitchOperation> LoadBoundOperationAsync(
            IProfileSwitchUnitOfWork dbContext,
            Guid switchId,
            ProfileSwitchRequestContext requestContext,
            CancellationToken cancellationToken)
        {
            var operation = await dbContext.FindOperationAsync(switchId, cancellationToken).ConfigureAwait(false);
            if (operation is null)
            {
                throw NotFound();
            }

            EnsureBoundRequest(operation, requestContext);
            return operation;
        }

        private async Task<ProfileSelector?> LoadSelectorForCallerAsync(
            IProfileSwitchUnitOfWork dbContext,
            Guid callerUserId,
            CancellationToken cancellationToken)
            => await dbContext.FindSelectorForCallerAsync(callerUserId, cancellationToken).ConfigureAwait(false);

        private async Task<User> LoadTargetUserPolicyAsync(
            IProfileSwitchUnitOfWork dbContext,
            Guid targetUserId,
            CancellationToken cancellationToken)
        {
            var targetUser = await dbContext.FindUserPolicyAsync(targetUserId, cancellationToken).ConfigureAwait(false);
            return targetUser ?? throw new ProfileSelectorException(
                (int)HttpStatusCode.Forbidden,
                "PROFILE_USER_NOT_FOUND",
                "The requested profile user no longer exists.");
        }

        private async Task<ProfileSelector> LoadSelectorByIdAsync(
            IProfileSwitchUnitOfWork dbContext,
            Guid selectorId,
            CancellationToken cancellationToken)
        {
            var selector = await dbContext.FindSelectorAsync(selectorId, cancellationToken).ConfigureAwait(false);
            return selector ?? throw new ProfileSelectorException(
                (int)HttpStatusCode.NotFound,
                "PROFILE_SELECTOR_NOT_CONFIGURED",
                "The prepared profile selector no longer exists.");
        }

        private static async Task EnsurePlaybackSettledAsync(
            IProfileSwitchUnitOfWork dbContext,
            Guid switchId,
            CancellationToken cancellationToken)
        {
            var hasUnclassifiedReport = await dbContext.HasUnclassifiedPlaybackReportAsync(
                switchId,
                cancellationToken).ConfigureAwait(false);
            if (hasUnclassifiedReport)
            {
                throw Conflict(
                    "PROFILE_SWITCH_PLAYBACK_NOT_SETTLED",
                    "The captured old playback report has no safe classified outcome.");
            }
        }

        private async Task EnsureOperationQuotaAsync(
            IProfileSwitchUnitOfWork dbContext,
            ProfileSwitchRequestContext requestContext,
            DateTime now,
            CancellationToken cancellationToken)
        {
            var retainedCount = await dbContext.CountRetainedOperationsAsync(
                requestContext.CurrentUserId,
                requestContext.DeviceId!,
                now,
                cancellationToken).ConfigureAwait(false);
            if (retainedCount >= _options.MaxRetainedOperationsPerCallerDevice)
            {
                throw TooManyRequests(
                    "PROFILE_SWITCH_OPERATION_LIMIT",
                    "The retained profile-switch quota for this caller and device has been reached.");
            }
        }

        private async Task RunMaintenanceAsync(DateTime now, CancellationToken cancellationToken)
        {
            var maintenanceContext = await _profileSwitchStore.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = maintenanceContext.ConfigureAwait(false);
            await using var transaction = await maintenanceContext.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await maintenanceContext.RunMaintenanceAsync(
                now,
                _options.MaintenanceBatchSize,
                cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task FailPlaybackReceiptAsync(
            IProfileSwitchUnitOfWork dbContext,
            ProfileSelectorPlaybackStopReport receipt)
        {
            receipt.State = ProfileSelectorPlaybackStopReportState.Failed;
            receipt.DateModifiedUtc = GetUtcNow();
            await dbContext.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }

        private async Task ValidatePreparedPinAsync(
            IProfileSwitchUnitOfWork dbContext,
            ProfileSelectorMember targetMember,
            string? pin,
            DateTime now,
            CancellationToken cancellationToken)
        {
            EnsureTargetNotLocked(targetMember, now);
            if (!string.IsNullOrEmpty(pin))
            {
                ValidatePin(pin);
            }

            if (string.IsNullOrEmpty(targetMember.PinHash))
            {
                return;
            }

            if (string.IsNullOrEmpty(pin))
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Conflict,
                    "PROFILE_PIN_REQUIRED",
                    "This profile requires a PIN.");
            }

            if (_cryptoProvider.Verify(PasswordHash.Parse(targetMember.PinHash), pin))
            {
                return;
            }

            RegisterInvalidPin(targetMember, now);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (targetMember.PinLockoutUntilUtc.HasValue && targetMember.PinLockoutUntilUtc.Value > now)
            {
                throw new ProfileSelectorException(423, "PROFILE_PIN_LOCKED", "This profile is temporarily locked due to invalid PIN attempts.");
            }

            throw new ProfileSelectorException(
                (int)HttpStatusCode.Forbidden,
                "PROFILE_PIN_INVALID",
                "The supplied profile PIN is invalid.");
        }

        private void EnsurePrepareReplayMatches(
            ProfileSelectorSwitchOperation operation,
            ProfileSwitchPrepareContext context)
        {
            if (!operation.TargetProfileUserId.Equals(context.TargetProfileUserId))
            {
                throw Conflict("PROFILE_SWITCH_ID_CONFLICT", "The switch id is already bound to another target profile.");
            }

            if (operation.PinProofHash is null)
            {
                if (!string.IsNullOrEmpty(context.Pin))
                {
                    throw Conflict("PROFILE_SWITCH_ID_CONFLICT", "The switch id is already bound to a different prepare payload.");
                }

                return;
            }

            if (string.IsNullOrEmpty(context.Pin))
            {
                throw Conflict("PROFILE_SWITCH_ID_CONFLICT", "The switch id is already bound to a different prepare payload.");
            }

            ValidatePin(context.Pin);
            if (!_cryptoProvider.Verify(PasswordHash.Parse(operation.PinProofHash), context.Pin))
            {
                throw Conflict("PROFILE_SWITCH_ID_CONFLICT", "The switch id is already bound to a different prepare payload.");
            }
        }

        private void EnsureTargetEligible(User targetUser, string deviceId, string remoteEndPoint, DateTime now)
        {
            if (targetUser.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_USER_DISABLED", "The selected profile is disabled.");
            }

            if (!targetUser.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableRemoteAccess)
                && !_networkManager.IsInLocalNetwork(remoteEndPoint))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_REMOTE_ACCESS_DENIED", "The selected profile cannot be activated from a remote network.");
            }

            if (!targetUser.IsParentalScheduleAllowed(now))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_ACCESS_SCHEDULE_DENIED", "The selected profile is not allowed access at this time.");
            }

            if (!_deviceManager.CanAccessDevice(targetUser, deviceId))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_DEVICE_ACCESS_DENIED", "The selected profile cannot access this device.");
            }

        }

        private async Task EnsureTargetSessionPolicyAsync(User targetUser, string deviceId)
        {
            try
            {
                await _sessionManager.ValidateProfileSwitchSessionPolicyAsync(targetUser, deviceId).ConfigureAwait(false);
            }
            catch (SecurityException)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Forbidden,
                    "PROFILE_MAX_SESSIONS_REACHED",
                    "The selected profile is already at its maximum number of sessions.");
            }
        }

        private static void RevalidateReservedSessionPolicy(
            IProfileSwitchCredentialReservation credentialReservation,
            User targetUser)
        {
            try
            {
                credentialReservation.RevalidateSessionPolicy(targetUser);
            }
            catch (SecurityException)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Forbidden,
                    "PROFILE_MAX_SESSIONS_REACHED",
                    "The selected profile is already at its maximum number of sessions.");
            }
        }

        private static void EnsureSelectorEnabled(ProfileSelector? selector)
        {
            if (selector is null || !selector.IsEnabled)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.NotFound,
                    "PROFILE_SELECTOR_NOT_CONFIGURED",
                    "No enabled profile selector is configured for the current user.");
            }
        }

        private static void EnsureCallerAuthority(ProfileSelector selector, Guid callerUserId)
        {
            if (!selector.OwnerUserId.Equals(callerUserId)
                && selector.Members.All(member => !member.ProfileUserId.Equals(callerUserId)))
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Forbidden,
                    "PROFILE_SWITCH_CALLER_NOT_AUTHORIZED",
                    "The prepared caller no longer belongs to this profile selector.");
            }
        }

        private static ProfileSelectorMember EnsureTargetMember(ProfileSelector selector, Guid targetUserId)
        {
            var targetMember = selector.Members.FirstOrDefault(member => member.ProfileUserId.Equals(targetUserId));
            if (targetMember is null)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Forbidden,
                    "PROFILE_NOT_LINKED",
                    "The requested profile does not belong to the current selector.");
            }

            if (!targetMember.IsVisible)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Forbidden,
                    "PROFILE_NOT_VISIBLE",
                    "The requested profile is not visible in the current selector.");
            }

            return targetMember;
        }

        private static void EnsureNotRedundantMemberSwitch(
            ProfileSelector selector,
            Guid callerUserId,
            Guid targetUserId)
        {
            if (callerUserId.Equals(targetUserId) && !selector.OwnerUserId.Equals(callerUserId))
            {
                throw Conflict(
                    "PROFILE_SWITCH_ALREADY_ACTIVE",
                    "A secondary profile cannot switch to its already active identity.");
            }
        }

        private User GetTargetUser(Guid targetUserId)
        {
            var targetUser = _userManager.GetUserById(targetUserId);
            return targetUser ?? throw new ProfileSelectorException(
                (int)HttpStatusCode.Forbidden,
                "PROFILE_USER_NOT_FOUND",
                "The requested profile user no longer exists.");
        }

        private static void EnsureTargetNotLocked(ProfileSelectorMember targetMember, DateTime now)
        {
            if (targetMember.PinLockoutUntilUtc.HasValue && targetMember.PinLockoutUntilUtc.Value > now)
            {
                throw new ProfileSelectorException(423, "PROFILE_PIN_LOCKED", "This profile is temporarily locked due to invalid PIN attempts.");
            }
        }

        private static void EnsureBoundRequest(
            ProfileSelectorSwitchOperation operation,
            ProfileSwitchRequestContext requestContext)
        {
            if (!operation.CallerUserId.Equals(requestContext.CurrentUserId)
                || operation.CallerCredentialRecordId != requestContext.CallerCredentialRecordId
                || !string.Equals(operation.DeviceId, requestContext.DeviceId, StringComparison.Ordinal))
            {
                throw NotFound();
            }
        }

        private static void EnsurePrepared(ProfileSelectorSwitchOperation operation)
        {
            if (operation.State is ProfileSelectorSwitchOperationState.Expired)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.Gone,
                    "PROFILE_SWITCH_EXPIRED",
                    "The prepared profile switch expired before commit.");
            }

            if (operation.State is ProfileSelectorSwitchOperationState.Aborted)
            {
                throw Conflict("PROFILE_SWITCH_ABORTED", "The profile switch was aborted before commit.");
            }

            if (operation.State is not ProfileSelectorSwitchOperationState.Prepared)
            {
                throw Conflict("PROFILE_SWITCH_INVALID_STATE", "The profile switch is not prepared.");
            }
        }

        private async Task ExpireIfNeededAsync(
            IProfileSwitchUnitOfWork dbContext,
            ProfileSelectorSwitchOperation operation,
            DateTime now,
            CancellationToken cancellationToken)
        {
            if (operation.State is not ProfileSelectorSwitchOperationState.Prepared
                || operation.PreparedExpiresUtc > now)
            {
                return;
            }

            operation.State = ProfileSelectorSwitchOperationState.Expired;
            operation.ActiveDeviceId = null;
            operation.DateModifiedUtc = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task EnsureRetainedAsync(
            IProfileSwitchUnitOfWork dbContext,
            ProfileSelectorSwitchOperation operation,
            DateTime now,
            CancellationToken cancellationToken)
        {
            if (operation.RetainUntilUtc > now)
            {
                return;
            }

            dbContext.RemoveOperation(operation);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            throw NotFound();
        }

        private async Task AbortBeforeCommitAsync(
            IProfileSwitchUnitOfWork dbContext,
            ProfileSelectorSwitchOperation operation,
            DateTime now,
            CancellationToken cancellationToken)
        {
            operation.State = ProfileSelectorSwitchOperationState.Aborted;
            operation.ActiveDeviceId = null;
            operation.DateModifiedUtc = now;
            operation.RetainUntilUtc = now.Add(_options.TerminalRetention);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await RemovePreparedOrphanCredentialsAsync(operation.SwitchId).ConfigureAwait(false);
        }

        private async Task RemovePreparedOrphanCredentialsAsync(Guid switchId)
        {
            var orphanedCredentials = _deviceManager.GetDevices(new DeviceQuery { ProfileSwitchId = switchId }).Items;
            foreach (var orphanedCredential in orphanedCredentials)
            {
                await _sessionManager.RevokeProfileSwitchCredential(orphanedCredential).ConfigureAwait(false);
            }
        }

        private async Task RemoveSupersededTargetRuntimeCredentialsAsync(
            Device replacementCredential,
            bool preserveUnmarkedRecoveryCredentials)
        {
            if (!replacementCredential.ProfileSwitchId.HasValue)
            {
                throw new InvalidOperationException("A reconciled profile-switch credential must identify its durable switch.");
            }

            var replacementSwitchId = replacementCredential.ProfileSwitchId.Value;
            var supersededCredentials = _deviceManager.GetDevices(
                new DeviceQuery
                {
                    UserId = replacementCredential.UserId,
                    DeviceId = replacementCredential.DeviceId
                }).Items
                .Where(device => device.Id != replacementCredential.Id
                                 && (device.ProfileSwitchId.HasValue
                                     ? !device.ProfileSwitchId.Value.Equals(replacementSwitchId)
                                     : !preserveUnmarkedRecoveryCredentials))
                .ToList();
            foreach (var supersededCredential in supersededCredentials)
            {
                await _sessionManager.RevokeSupersededProfileCredential(
                    supersededCredential,
                    replacementSwitchId).ConfigureAwait(false);
            }
        }

        private Task RevokeProfileSwitchCredentialAsync(Device credential)
            => credential.Id > 0 && credential.ProfileSwitchId.HasValue
                ? _sessionManager.RevokeProfileSwitchCredential(credential)
                : Task.CompletedTask;

        private static AuthenticationRequest CreateAuthenticationRequest(
            ProfileSelectorSwitchOperation operation,
            string remoteEndPoint)
            => new()
            {
                UserId = operation.TargetProfileUserId,
                DeviceId = operation.DeviceId,
                DeviceName = operation.DeviceName,
                App = operation.Client,
                AppVersion = operation.Version,
                RemoteEndPoint = remoteEndPoint
            };

        private static ProfileSwitchResult MapResult(
            ProfileSelectorSwitchOperation operation,
            AuthenticationResult? authenticationResult = null)
            => new()
            {
                SwitchId = operation.SwitchId,
                ProfileSelectorId = operation.ProfileSelectorId,
                OwnerUserId = operation.OwnerUserId,
                TargetProfileUserId = operation.TargetProfileUserId,
                State = operation.State switch
                {
                    ProfileSelectorSwitchOperationState.Prepared => ProfileSwitchState.Prepared,
                    ProfileSelectorSwitchOperationState.Committed => ProfileSwitchState.Committed,
                    ProfileSelectorSwitchOperationState.Expired => ProfileSwitchState.Expired,
                    ProfileSelectorSwitchOperationState.Aborted => ProfileSwitchState.Aborted,
                    _ => throw new InvalidOperationException("Unsupported profile switch state.")
                },
                PreparedExpiresUtc = operation.PreparedExpiresUtc,
                AuthenticationResult = authenticationResult
            };

        private static ProfileSwitchPlaybackStopResult MapPlaybackResult(ProfileSelectorPlaybackStopReport receipt)
            => new()
            {
                ReportKey = receipt.ReportKey,
                Outcome = receipt.State switch
                {
                    ProfileSelectorPlaybackStopReportState.NotActive => ProfileSwitchPlaybackStopOutcome.NotActive,
                    ProfileSelectorPlaybackStopReportState.Acknowledged => ProfileSwitchPlaybackStopOutcome.Acknowledged,
                    ProfileSelectorPlaybackStopReportState.Processing => ProfileSwitchPlaybackStopOutcome.Failed,
                    ProfileSelectorPlaybackStopReportState.Failed => ProfileSwitchPlaybackStopOutcome.Failed,
                    _ => throw new InvalidOperationException("Unsupported profile switch playback receipt state.")
                }
            };

        private static void EnsurePlaybackReplayMatches(
            ProfileSelectorPlaybackStopReport receipt,
            ProfileSelectorSwitchOperation operation,
            string requestHash)
        {
            if (!receipt.SwitchId.Equals(operation.SwitchId)
                || !receipt.CallerUserId.Equals(operation.CallerUserId)
                || !string.Equals(receipt.DeviceId, operation.DeviceId, StringComparison.Ordinal)
                || !string.Equals(receipt.RequestHash, requestHash, StringComparison.Ordinal))
            {
                throw Conflict(
                    "PROFILE_SWITCH_PLAYBACK_REPORT_CONFLICT",
                    "The playback report key is already bound to a different old-session payload.");
            }
        }

        private static string CreatePlaybackReportKey(Guid switchId, string playSessionId)
            => Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes($"{switchId:N}:{playSessionId}")))
                .ToLowerInvariant();

        private static string CreatePlaybackRequestHash(PlaybackStopInfo playbackStopInfo)
        {
            var fingerprint = new StringBuilder();
            AppendFingerprint(fingerprint, playbackStopInfo.ItemId.ToString("N", CultureInfo.InvariantCulture));
            AppendFingerprint(fingerprint, playbackStopInfo.PositionTicks?.ToString(CultureInfo.InvariantCulture));
            AppendFingerprint(fingerprint, playbackStopInfo.PlaySessionId);
            AppendFingerprint(fingerprint, playbackStopInfo.Failed.ToString(CultureInfo.InvariantCulture));
            AppendFingerprint(fingerprint, playbackStopInfo.NextMediaType);

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint.ToString()))).ToLowerInvariant();
        }

        private static void AppendFingerprint(StringBuilder builder, string? value)
        {
            if (value is null)
            {
                builder.Append("-1:");
                return;
            }

            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
        }

        private static void ValidatePrepareContext(ProfileSwitchPrepareContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            ValidateSwitchId(context.SwitchId);
            if (context.TargetProfileUserId == Guid.Empty)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_SWITCH_TARGET_REQUIRED",
                    "TargetProfileUserId must not be empty.");
            }

            if (context.RequestContext is null)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_SWITCH_CONTEXT_REQUIRED",
                    "Authenticated profile switch request context is required.");
            }

            ValidateRequestContext(context.RequestContext);
        }

        private static void ValidateRequestContext(ProfileSwitchRequestContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (context.CurrentUserId == Guid.Empty)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_SWITCH_CALLER_REQUIRED",
                    "The authenticated caller user id is required.");
            }

            if (context.CallerCredentialRecordId <= 0)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_SWITCH_CREDENTIAL_REQUIRED",
                    "A persisted caller credential is required.");
            }

            ValidateBoundText(context.DeviceId, 255, "DeviceId");
            ValidateBoundText(context.DeviceName, 64, "DeviceName");
            ValidateBoundText(context.Client, 64, "Client");
            ValidateBoundText(context.Version, 32, "Version");
            ValidateBoundText(context.RemoteEndPoint, 64, "RemoteEndPoint");
        }

        private static void ValidateBoundText(string? value, int maxLength, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_SWITCH_CONTEXT_INVALID",
                    $"{fieldName} is required and must not exceed {maxLength.ToString(CultureInfo.InvariantCulture)} characters.");
            }
        }

        private static void ValidateSwitchId(Guid switchId)
        {
            if (switchId == Guid.Empty)
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_SWITCH_ID_REQUIRED",
                    "SwitchId must not be empty.");
            }
        }

        private static void ValidatePin(string pin)
        {
            if (pin.Length < MinPinLength
                || pin.Length > MaxPinLength
                || pin.Any(character => character is < '0' or > '9'))
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_PIN_INVALID_FORMAT",
                    $"Profile PIN must be {MinPinLength}-{MaxPinLength} ASCII digits.");
            }
        }

        private void ValidatePlaybackStop(PlaybackStopInfo playbackStopInfo)
        {
            ArgumentNullException.ThrowIfNull(playbackStopInfo);
            if (string.IsNullOrWhiteSpace(playbackStopInfo.PlaySessionId)
                || playbackStopInfo.PlaySessionId.Length > 255
                || playbackStopInfo.ItemId == Guid.Empty
                || !playbackStopInfo.PositionTicks.HasValue
                || playbackStopInfo.PositionTicks.Value < 0
                || ExceedsLength(playbackStopInfo.NextMediaType, 32))
            {
                throw new ProfileSelectorException(
                    (int)HttpStatusCode.BadRequest,
                    "PROFILE_SWITCH_PLAYBACK_REPORT_INVALID",
                    "A non-empty play session, item, authenticated session and non-negative position are required.");
            }
        }

        private static bool ExceedsLength(string? value, int maxLength)
            => value?.Length > maxLength;

        private static void RegisterInvalidPin(ProfileSelectorMember member, DateTime now)
        {
            member.FailedPinAttemptCount++;
            member.LastFailedPinAttemptUtc = now;
            if (member.FailedPinAttemptCount < PinFailureBackoffThreshold)
            {
                member.PinLockoutUntilUtc = null;
                return;
            }

            var penaltySteps = member.FailedPinAttemptCount - (PinFailureBackoffThreshold - 1);
            member.PinLockoutUntilUtc = now.AddSeconds(Math.Min(penaltySteps * 30, 300));
        }

        private static ProfileSelectorException Conflict(string code, string message)
            => new((int)HttpStatusCode.Conflict, code, message);

        private static ProfileSelectorException TooManyRequests(string code, string message)
            => new((int)HttpStatusCode.TooManyRequests, code, message);

        private static ProfileSelectorException NotFound()
            => new((int)HttpStatusCode.NotFound, "PROFILE_SWITCH_NOT_FOUND", "No profile switch is available for this caller and device.");

        private DateTime GetUtcNow()
            => _timeProvider.GetUtcNow().UtcDateTime;

        private async Task<T> ExecuteLockedAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
        {
            await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return await action().ConfigureAwait(false);
            }
            finally
            {
                _operationLock.Release();
            }
        }
    }
}
