using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Jellyfin.Api.Models.ProfileSelectorsDtos;

/// <summary>
/// Full replacement payload for a profile selector configuration.
/// </summary>
public class ProfileSelectorUpdateRequestDto
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ProfileSelectorUpdateRequestDto"/> class.
    /// </summary>
    public ProfileSelectorUpdateRequestDto()
    {
        Profiles = new Collection<ProfileSelectorMemberUpdateRequestDto>();
    }

    /// <summary>
    /// Gets or sets a value indicating whether the selector is enabled.
    /// </summary>
    public bool IsEnabled { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the client may auto-select one visible profile.
    /// </summary>
    public bool AutoSelectSingleProfile { get; set; }

    /// <summary>
    /// Gets or sets the desired selector members.
    /// </summary>
    [Required]
    [JsonRequired]
    public ICollection<ProfileSelectorMemberUpdateRequestDto> Profiles { get; set; }
}
