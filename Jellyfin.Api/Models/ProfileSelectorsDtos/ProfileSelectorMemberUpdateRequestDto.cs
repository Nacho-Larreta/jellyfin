using System;
using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.ProfileSelectorsDtos;

/// <summary>
/// One selector member entry inside the update request.
/// </summary>
public class ProfileSelectorMemberUpdateRequestDto
{
    /// <summary>
    /// Gets or sets the profile user id.
    /// </summary>
    [Required]
    public Guid ProfileUserId { get; set; }

    /// <summary>
    /// Gets or sets the display order.
    /// </summary>
    public int DisplayOrder { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the profile is visible.
    /// </summary>
    public bool IsVisible { get; set; }
}
