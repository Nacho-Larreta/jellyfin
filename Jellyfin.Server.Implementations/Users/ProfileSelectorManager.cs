using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Authentication;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.ProfileSelectors;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Cryptography;
using MediaBrowser.Model.Dto;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Users
{
    /// <summary>
    /// Manages selector membership, device restore state and PIN-protected activation.
    /// </summary>
    public sealed class ProfileSelectorManager : IProfileSelectorManager
    {
        private const int MinPinLength = 4;
        private const int MaxPinLength = 8;
        private const int PinFailureBackoffThreshold = 3;

        private readonly IDbContextFactory<JellyfinDbContext> _dbContextFactory;
        private readonly IUserManager _userManager;
        private readonly IDeviceManager _deviceManager;
        private readonly ISessionManager _sessionManager;
        private readonly ICryptoProvider _cryptoProvider;
        private readonly INetworkManager _networkManager;

        /// <summary>
        /// Initializes a new instance of the <see cref="ProfileSelectorManager"/> class.
        /// </summary>
        /// <param name="dbContextFactory">The database context factory.</param>
        /// <param name="userManager">The user manager.</param>
        /// <param name="deviceManager">The device manager.</param>
        /// <param name="sessionManager">The session manager.</param>
        /// <param name="cryptoProvider">The crypto provider.</param>
        /// <param name="networkManager">The network manager.</param>
        public ProfileSelectorManager(
            IDbContextFactory<JellyfinDbContext> dbContextFactory,
            IUserManager userManager,
            IDeviceManager deviceManager,
            ISessionManager sessionManager,
            ICryptoProvider cryptoProvider,
            INetworkManager networkManager)
        {
            _dbContextFactory = dbContextFactory;
            _userManager = userManager;
            _deviceManager = deviceManager;
            _sessionManager = sessionManager;
            _cryptoProvider = cryptoProvider;
            _networkManager = networkManager;
        }

        /// <inheritdoc />
        public async Task<ProfileSelectorDto?> GetCurrentSelectorAsync(Guid currentUserId, string deviceId, bool includeHiddenProfiles, CancellationToken cancellationToken)
        {
            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var selector = await LoadSelectorForCurrentUserAsync(dbContext, currentUserId, cancellationToken).ConfigureAwait(false);
            if (selector is null || !selector.IsEnabled)
            {
                return null;
            }

            return BuildDto(selector, currentUserId, deviceId, includeHiddenProfiles);
        }

        /// <inheritdoc />
        public async Task<ProfileSelectorDto?> GetSelectorForOwnerAsync(Guid ownerUserId, string deviceId, bool includeHiddenProfiles, CancellationToken cancellationToken)
        {
            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var selector = await QuerySelectors(dbContext)
                .FirstOrDefaultAsync(entity => entity.OwnerUserId.Equals(ownerUserId), cancellationToken)
                .ConfigureAwait(false);

            return selector is null
                ? null
                : BuildDto(selector, ownerUserId, deviceId, includeHiddenProfiles);
        }

        /// <inheritdoc />
        public async Task<bool> IsProfileLinkedToOwnerAsync(Guid ownerUserId, Guid profileUserId, CancellationToken cancellationToken)
        {
            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);

            return await dbContext.ProfileSelectorMembers
                .AsNoTracking()
                .AnyAsync(
                    member => member.ProfileUserId.Equals(profileUserId)
                              && dbContext.ProfileSelectors.Any(
                                  selector => selector.Id.Equals(member.ProfileSelectorId)
                                              && selector.OwnerUserId.Equals(ownerUserId)),
                    cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<IReadOnlyList<Guid>> GetSecondaryProfileUserIdsAsync(CancellationToken cancellationToken)
        {
            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);

            return await dbContext.ProfileSelectorMembers
                .AsNoTracking()
                .Join(
                    dbContext.ProfileSelectors.AsNoTracking(),
                    member => member.ProfileSelectorId,
                    selector => selector.Id,
                    (member, selector) => new
                    {
                        member.ProfileUserId,
                        selector.OwnerUserId
                    })
                .Where(profile => !profile.ProfileUserId.Equals(profile.OwnerUserId))
                .Select(profile => profile.ProfileUserId)
                .Distinct()
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task<ProfileSelectorDto> UpdateSelectorAsync(Guid ownerUserId, string deviceId, ProfileSelectorConfiguration configuration, CancellationToken cancellationToken)
        {
            var ownerUser = GetUserOrThrow(ownerUserId);
            EnsureOwnerUser(ownerUser);

            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var selector = await QuerySelectors(dbContext)
                .FirstOrDefaultAsync(entity => entity.OwnerUserId.Equals(ownerUserId), cancellationToken)
                .ConfigureAwait(false);

            var isNewSelector = selector is null;
            selector ??= new ProfileSelector(ownerUserId);

            NormalizeConfiguration(configuration, ownerUserId);
            ValidateRequestedProfiles(selector.Id, configuration);

            selector.IsEnabled = configuration.IsEnabled;
            selector.AutoSelectSingleProfile = configuration.AutoSelectSingleProfile;
            selector.DateModified = DateTime.UtcNow;

            var desiredMembers = configuration.Profiles.ToDictionary(profile => profile.ProfileUserId);
            var existingMembers = selector.Members.ToDictionary(member => member.ProfileUserId);

            foreach (var existingMember in selector.Members.Where(member => !desiredMembers.ContainsKey(member.ProfileUserId)).ToList())
            {
                selector.Members.Remove(existingMember);
                dbContext.ProfileSelectorMembers.Remove(existingMember);
            }

            foreach (var (profileUserId, desiredMember) in desiredMembers)
            {
                _ = GetUserOrThrow(profileUserId);

                if (!existingMembers.TryGetValue(profileUserId, out var member))
                {
                    member = new ProfileSelectorMember(selector.Id, profileUserId);
                    selector.Members.Add(member);
                }

                member.DisplayOrder = desiredMember.DisplayOrder;
                member.IsVisible = desiredMember.IsVisible;
            }

            foreach (var deviceState in selector.DeviceStates.Where(state => state.ActiveProfileUserId.HasValue && !desiredMembers.ContainsKey(state.ActiveProfileUserId.Value)))
            {
                deviceState.ActiveProfileUserId = null;
                deviceState.LastActivatedUtc = null;
            }

            if (isNewSelector)
            {
                dbContext.ProfileSelectors.Add(selector);
            }

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return BuildDto(selector, ownerUserId, deviceId, true);
        }

        /// <inheritdoc />
        public async Task<ProfileActivationResult> ActivateProfileAsync(ProfileActivationContext context, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentException.ThrowIfNullOrEmpty(context.DeviceId);
            ArgumentException.ThrowIfNullOrEmpty(context.DeviceName);
            ArgumentException.ThrowIfNullOrEmpty(context.Client);
            ArgumentException.ThrowIfNullOrEmpty(context.Version);
            ArgumentException.ThrowIfNullOrEmpty(context.RemoteEndPoint);

            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var selector = await LoadSelectorForCurrentUserAsync(dbContext, context.CurrentUserId, cancellationToken).ConfigureAwait(false);
            if (selector is null || !selector.IsEnabled)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.NotFound, "PROFILE_SELECTOR_NOT_CONFIGURED", "No profile selector is configured for the current user.");
            }

            var selectorMember = selector.Members.FirstOrDefault(member => member.ProfileUserId.Equals(context.ProfileUserId));
            if (selectorMember is null)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_NOT_LINKED", "The requested profile does not belong to the current selector.");
            }

            var now = DateTime.UtcNow;
            if (selectorMember.PinLockoutUntilUtc.HasValue && selectorMember.PinLockoutUntilUtc.Value > now)
            {
                throw new ProfileSelectorException(423, "PROFILE_PIN_LOCKED", "This profile is temporarily locked due to invalid PIN attempts.");
            }

            if (!string.IsNullOrEmpty(selectorMember.PinHash))
            {
                if (string.IsNullOrWhiteSpace(context.Pin))
                {
                    throw new ProfileSelectorException((int)HttpStatusCode.Conflict, "PROFILE_PIN_REQUIRED", "This profile requires a PIN.");
                }

                var pinHash = PasswordHash.Parse(selectorMember.PinHash);
                if (!_cryptoProvider.Verify(pinHash, context.Pin))
                {
                    RegisterInvalidPin(selectorMember, now);
                    await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    if (selectorMember.PinLockoutUntilUtc.HasValue && selectorMember.PinLockoutUntilUtc.Value > now)
                    {
                        throw new ProfileSelectorException(423, "PROFILE_PIN_LOCKED", "This profile is temporarily locked due to invalid PIN attempts.");
                    }

                    throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_PIN_INVALID", "The supplied profile PIN is invalid.");
                }
            }

            var profileUser = GetUserOrThrow(context.ProfileUserId);
            EnsureProfileCanActivate(profileUser, context.DeviceId, context.RemoteEndPoint);

            var activeSessions = _sessionManager.Sessions.Count(session => session.UserId.Equals(profileUser.Id));
            var maxActiveSessions = profileUser.MaxActiveSessions;
            if (maxActiveSessions >= 1 && activeSessions >= maxActiveSessions)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_MAX_SESSIONS_REACHED", "The selected profile is already at its maximum number of sessions.");
            }

            AuthenticationResult authenticationResult;
            try
            {
                authenticationResult = await _sessionManager.AuthenticateDirect(
                    new AuthenticationRequest
                    {
                        UserId = profileUser.Id,
                        DeviceId = context.DeviceId,
                        DeviceName = context.DeviceName,
                        App = context.Client,
                        AppVersion = context.Version,
                        RemoteEndPoint = context.RemoteEndPoint
                    }).ConfigureAwait(false);
            }
            catch (AuthenticationException)
            {
                throw;
            }
            catch (SecurityException ex)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_ACTIVATION_FORBIDDEN", ex.Message);
            }

            selectorMember.FailedPinAttemptCount = 0;
            selectorMember.PinLockoutUntilUtc = null;
            selectorMember.LastFailedPinAttemptUtc = null;
            selector.DateModified = now;

            var deviceState = selector.DeviceStates.FirstOrDefault(state => string.Equals(state.DeviceId, context.DeviceId, StringComparison.Ordinal));
            if (deviceState is null)
            {
                deviceState = new ProfileSelectorDeviceState(selector.Id, context.DeviceId);
                selector.DeviceStates.Add(deviceState);
            }

            deviceState.ActiveProfileUserId = profileUser.Id;
            deviceState.LastActivatedUtc = now;

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return new ProfileActivationResult
            {
                ProfileSelectorId = selector.Id,
                OwnerUserId = selector.OwnerUserId,
                ActiveProfileUserId = profileUser.Id,
                AuthenticationResult = authenticationResult
            };
        }

        /// <inheritdoc />
        public async Task SetProfilePinAsync(Guid ownerUserId, Guid profileUserId, string pin, CancellationToken cancellationToken)
        {
            ValidatePin(pin);

            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var selector = await QuerySelectors(dbContext)
                .FirstOrDefaultAsync(entity => entity.OwnerUserId.Equals(ownerUserId), cancellationToken)
                .ConfigureAwait(false);

            if (selector is null)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.NotFound, "PROFILE_SELECTOR_NOT_CONFIGURED", "No profile selector is configured for the requested owner.");
            }

            EnsureOwnerUser(GetUserOrThrow(ownerUserId));

            var member = selector.Members.FirstOrDefault(entity => entity.ProfileUserId.Equals(profileUserId));
            if (member is null)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_NOT_LINKED", "The requested profile does not belong to the selector.");
            }

            member.PinHash = _cryptoProvider.CreatePasswordHash(pin).ToString();
            member.FailedPinAttemptCount = 0;
            member.PinLockoutUntilUtc = null;
            member.LastFailedPinAttemptUtc = null;
            selector.DateModified = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <inheritdoc />
        public async Task ClearProfilePinAsync(Guid ownerUserId, Guid profileUserId, string? currentPin, CancellationToken cancellationToken)
        {
            var dbContext = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using var configuredContext = dbContext.ConfigureAwait(false);
            var selector = await QuerySelectors(dbContext)
                .FirstOrDefaultAsync(entity => entity.OwnerUserId.Equals(ownerUserId), cancellationToken)
                .ConfigureAwait(false);

            if (selector is null)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.NotFound, "PROFILE_SELECTOR_NOT_CONFIGURED", "No profile selector is configured for the requested owner.");
            }

            EnsureOwnerUser(GetUserOrThrow(ownerUserId));

            var member = selector.Members.FirstOrDefault(entity => entity.ProfileUserId.Equals(profileUserId));
            if (member is null)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_NOT_LINKED", "The requested profile does not belong to the selector.");
            }

            if (!string.IsNullOrEmpty(currentPin))
            {
                ValidateProfilePin(member, currentPin);
            }

            member.PinHash = null;
            member.FailedPinAttemptCount = 0;
            member.PinLockoutUntilUtc = null;
            member.LastFailedPinAttemptUtc = null;
            selector.DateModified = DateTime.UtcNow;

            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        private static IQueryable<ProfileSelector> QuerySelectors(JellyfinDbContext dbContext)
            => dbContext.ProfileSelectors
                .Include(entity => entity.Members)
                .Include(entity => entity.DeviceStates);

        private async Task<ProfileSelector?> LoadSelectorForCurrentUserAsync(JellyfinDbContext dbContext, Guid currentUserId, CancellationToken cancellationToken)
        {
            var ownedSelector = await QuerySelectors(dbContext)
                .FirstOrDefaultAsync(entity => entity.OwnerUserId.Equals(currentUserId), cancellationToken)
                .ConfigureAwait(false);
            if (ownedSelector is not null)
            {
                return ownedSelector;
            }

            var membership = await dbContext.ProfileSelectorMembers
                .AsNoTracking()
                .FirstOrDefaultAsync(entity => entity.ProfileUserId.Equals(currentUserId), cancellationToken)
                .ConfigureAwait(false);

            return membership is null
                ? null
                : await QuerySelectors(dbContext)
                    .FirstOrDefaultAsync(entity => entity.Id.Equals(membership.ProfileSelectorId), cancellationToken)
                    .ConfigureAwait(false);
        }

        private ProfileSelectorDto BuildDto(ProfileSelector selector, Guid currentUserId, string deviceId, bool includeHiddenProfiles)
        {
            var currentUser = _userManager.GetUserById(currentUserId);
            var ownerUser = _userManager.GetUserById(selector.OwnerUserId);
            var currentState = selector.DeviceStates.FirstOrDefault(state => string.Equals(state.DeviceId, deviceId, StringComparison.Ordinal));
            var currentMember = currentState?.ActiveProfileUserId.HasValue == true
                ? selector.Members.FirstOrDefault(member => member.ProfileUserId.Equals(currentState.ActiveProfileUserId.Value))
                : null;

            var dto = new ProfileSelectorDto
            {
                ProfileSelectorId = selector.Id,
                OwnerUserId = selector.OwnerUserId,
                OwnerUserName = ownerUser?.Username,
                IsEnabled = selector.IsEnabled,
                AutoSelectSingleProfile = selector.AutoSelectSingleProfile,
                CanManageProfiles = selector.OwnerUserId.Equals(currentUserId) || currentUser?.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator) == true,
                IsCurrentUserOwner = selector.OwnerUserId.Equals(currentUserId),
                CurrentDeviceProfileUserId = currentState?.ActiveProfileUserId,
                CurrentDeviceProfileRequiresPin = !string.IsNullOrEmpty(currentMember?.PinHash)
            };

            foreach (var member in selector.Members.OrderBy(entity => entity.DisplayOrder).ThenBy(entity => entity.ProfileUserId))
            {
                if (!includeHiddenProfiles && !member.IsVisible)
                {
                    continue;
                }

                var user = _userManager.GetUserById(member.ProfileUserId);
                if (user is null)
                {
                    continue;
                }

                var userDto = _userManager.GetUserDto(user);
                dto.Profiles.Add(new ProfileSelectorMemberDto
                {
                    ProfileUserId = user.Id,
                    Name = userDto.Name,
                    PrimaryImageTag = userDto.PrimaryImageTag,
                    DisplayOrder = member.DisplayOrder,
                    IsVisible = member.IsVisible,
                    RequiresPin = !string.IsNullOrEmpty(member.PinHash),
                    IsDisabled = userDto.Policy.IsDisabled,
                    IsAdministrator = userDto.Policy.IsAdministrator,
                    IsOwner = user.Id.Equals(selector.OwnerUserId),
                    HasParentalRestrictions = HasParentalRestrictions(userDto),
                    IsActive = currentState?.ActiveProfileUserId.Equals(user.Id) == true,
                    LastLoginDate = userDto.LastLoginDate,
                    LastActivityDate = userDto.LastActivityDate
                });
            }

            return dto;
        }

        private static bool HasParentalRestrictions(UserDto user)
            => user.Policy.MaxParentalRating.HasValue
               || user.Policy.MaxParentalSubRating.HasValue
               || user.Policy.BlockedTags.Length > 0
               || !user.Policy.EnableAllFolders
               || user.Policy.BlockedMediaFolders.Length > 0
               || user.Policy.BlockUnratedItems.Length > 0;

        private static void ValidatePin(string pin)
        {
            if (string.IsNullOrWhiteSpace(pin) || pin.Length < MinPinLength || pin.Length > MaxPinLength || !pin.All(char.IsDigit))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.BadRequest, "PROFILE_PIN_INVALID_FORMAT", $"Profile PIN must be {MinPinLength}-{MaxPinLength} digits.");
            }
        }

        private void ValidateProfilePin(ProfileSelectorMember member, string pin)
        {
            if (string.IsNullOrEmpty(member.PinHash))
            {
                return;
            }

            ValidatePin(pin);
            var pinHash = PasswordHash.Parse(member.PinHash);
            if (!_cryptoProvider.Verify(pinHash, pin))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_PIN_INVALID", "The supplied profile PIN is invalid.");
            }
        }

        private void ValidateRequestedProfiles(Guid selectorId, ProfileSelectorConfiguration configuration)
        {
            var duplicateProfileIds = configuration.Profiles
                .GroupBy(profile => profile.ProfileUserId)
                .Where(group => group.Count() > 1)
                .Select(group => group.Key)
                .ToList();
            if (duplicateProfileIds.Count > 0)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.BadRequest, "PROFILE_DUPLICATE_MEMBERS", "The selector configuration contains duplicate profiles.");
            }

            var otherSelectorMembership = GetMemberships(configuration.Profiles.Select(profile => profile.ProfileUserId))
                .FirstOrDefault(membership => !membership.ProfileSelectorId.Equals(selectorId));
            if (otherSelectorMembership is not null)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.BadRequest, "PROFILE_ALREADY_LINKED", "One of the requested profiles already belongs to another selector.");
            }
        }

        private IEnumerable<ProfileSelectorMember> GetMemberships(IEnumerable<Guid> userIds)
        {
            var requestedUserIds = userIds.Distinct().ToArray();
            if (requestedUserIds.Length == 0)
            {
                return Array.Empty<ProfileSelectorMember>();
            }

            using var dbContext = _dbContextFactory.CreateDbContext();
            return dbContext.ProfileSelectorMembers
                .AsNoTracking()
                .Where(member => requestedUserIds.Contains(member.ProfileUserId))
                .ToArray();
        }

        private static void NormalizeConfiguration(ProfileSelectorConfiguration configuration, Guid ownerUserId)
        {
            if (configuration.Profiles.All(profile => !profile.ProfileUserId.Equals(ownerUserId)))
            {
                var nextOrder = configuration.Profiles.Count == 0
                    ? 0
                    : configuration.Profiles.Max(profile => profile.DisplayOrder) + 1;
                configuration.Profiles.Add(new ProfileSelectorMemberConfiguration
                {
                    ProfileUserId = ownerUserId,
                    DisplayOrder = nextOrder,
                    IsVisible = true
                });
            }
        }

        private static void RegisterInvalidPin(ProfileSelectorMember member, DateTime now)
        {
            member.FailedPinAttemptCount++;
            member.LastFailedPinAttemptUtc = now;

            if (member.FailedPinAttemptCount >= PinFailureBackoffThreshold)
            {
                var penaltySteps = member.FailedPinAttemptCount - (PinFailureBackoffThreshold - 1);
                var lockoutSeconds = Math.Min(penaltySteps * 30, 300);
                member.PinLockoutUntilUtc = now.AddSeconds(lockoutSeconds);
            }
            else
            {
                member.PinLockoutUntilUtc = null;
            }
        }

        private void EnsureProfileCanActivate(User profileUser, string deviceId, string remoteEndPoint)
        {
            if (profileUser.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsDisabled))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_USER_DISABLED", "The selected profile is disabled.");
            }

            if (!profileUser.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.EnableRemoteAccess)
                && !_networkManager.IsInLocalNetwork(remoteEndPoint))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_REMOTE_ACCESS_DENIED", "The selected profile cannot be activated from a remote network.");
            }

            if (!profileUser.IsParentalScheduleAllowed())
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_ACCESS_SCHEDULE_DENIED", "The selected profile is not allowed access at this time.");
            }

            if (!_deviceManager.CanAccessDevice(profileUser, deviceId))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_DEVICE_ACCESS_DENIED", "The selected profile cannot access this device.");
            }
        }

        private static void EnsureOwnerUser(User ownerUser)
        {
            if (!ownerUser.HasPermission(Jellyfin.Database.Implementations.Enums.PermissionKind.IsAdministrator))
            {
                throw new ProfileSelectorException((int)HttpStatusCode.Forbidden, "PROFILE_OWNER_REQUIRED", "Only administrator users can own a profile selector in this version.");
            }
        }

        private User GetUserOrThrow(Guid userId)
        {
            var user = _userManager.GetUserById(userId);
            if (user is null)
            {
                throw new ProfileSelectorException((int)HttpStatusCode.BadRequest, "PROFILE_USER_NOT_FOUND", "The requested user does not exist.");
            }

            return user;
        }
    }
}
