using System;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;

namespace MediaBrowser.Controller.Session;

/// <summary>
/// Owns one user-session admission slot while a profile-switch credential reaches its commit point.
/// </summary>
public interface IProfileSwitchCredentialReservation : IAsyncDisposable
{
    /// <summary>
    /// Gets the unpersisted target credential covered by this reservation.
    /// </summary>
    Device Credential { get; }

    /// <summary>
    /// Revalidates the mutable session-limit policy while this reservation still owns the user admission gate.
    /// </summary>
    /// <param name="user">The current durable target-user policy snapshot.</param>
    void RevalidateSessionPolicy(User user);
}
