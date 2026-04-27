namespace Jellyfin.Api.Models.ProfileSelectorsDtos;

/// <summary>
/// Request body for clearing a selector PIN with optional current PIN validation.
/// </summary>
public class ProfilePinClearRequestDto
{
    /// <summary>
    /// Gets or sets the current selector PIN.
    /// </summary>
    public string? Pin { get; set; }
}
