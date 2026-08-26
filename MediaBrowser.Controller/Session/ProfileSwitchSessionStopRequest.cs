using System;

namespace MediaBrowser.Controller.Session;

/// <summary>
/// Describes the old playback identity that a profile switch must compare and stop atomically.
/// </summary>
public sealed class ProfileSwitchSessionStopRequest
{
    /// <summary>
    /// Gets or sets the old profile user identifier.
    /// </summary>
    public Guid UserId { get; set; }

    /// <summary>
    /// Gets or sets the canonical persisted device identifier.
    /// </summary>
    public string? DeviceId { get; set; }

    /// <summary>
    /// Gets or sets the canonical persisted client name.
    /// </summary>
    public string? Client { get; set; }

    /// <summary>
    /// Gets or sets the captured item identifier.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the captured play-session identifier.
    /// </summary>
    public string? PlaySessionId { get; set; }

    /// <summary>
    /// Gets or sets the final playback position.
    /// </summary>
    public long PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether playback failed.
    /// </summary>
    public bool Failed { get; set; }

    /// <summary>
    /// Gets or sets the optional next-media classification.
    /// </summary>
    public string? NextMediaType { get; set; }
}
