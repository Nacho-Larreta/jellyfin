namespace Jellyfin.Api.Models.ProfileSelectorsDtos;

/// <summary>
/// Request body for activating a selector profile.
/// </summary>
public class ProfileActivationRequestDto
{
    /// <summary>
    /// Gets or sets the optional selector PIN.
    /// </summary>
    public string? Pin { get; set; }
}
