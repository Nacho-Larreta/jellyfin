using System;

namespace Jellyfin.Api.Models.ProfileSelectorsDtos;

/// <summary>
/// The bounded client-owned portion of a profile-switch playback stop report.
/// </summary>
public sealed class ProfileSwitchPlaybackStopRequestDto
{
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
    public long? PositionTicks { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether playback failed.
    /// </summary>
    public bool Failed { get; set; }

    /// <summary>
    /// Gets or sets the optional next-media classification.
    /// </summary>
    public string? NextMediaType { get; set; }
}
