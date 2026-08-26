using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.ProfileSelectorsDtos;

/// <summary>
/// The payload used to prepare a durable profile switch.
/// </summary>
public sealed class ProfileSwitchPrepareRequestDto
{
    /// <summary>
    /// Gets or sets the target profile user identifier.
    /// </summary>
    [Required]
    public Guid TargetProfileUserId { get; set; }

    /// <summary>
    /// Gets or sets the optional target profile PIN.
    /// </summary>
    public string? Pin { get; set; }
}
