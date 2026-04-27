using System.ComponentModel.DataAnnotations;

namespace Jellyfin.Api.Models.ProfileSelectorsDtos;

/// <summary>
/// Request body for creating or replacing a selector PIN.
/// </summary>
public class ProfilePinUpdateRequestDto
{
    /// <summary>
    /// Gets or sets the selector PIN.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string Pin { get; set; } = string.Empty;
}
