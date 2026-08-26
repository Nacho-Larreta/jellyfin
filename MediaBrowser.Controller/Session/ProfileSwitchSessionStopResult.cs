namespace MediaBrowser.Controller.Session;

/// <summary>
/// Classifies an atomic old-playback compare-and-stop operation.
/// </summary>
public enum ProfileSwitchSessionStopOutcome
{
    /// <summary>
    /// The canonical session has no active playback.
    /// </summary>
    NotActive,

    /// <summary>
    /// The exact captured playback was stopped.
    /// </summary>
    Stopped,

    /// <summary>
    /// The canonical session belongs to another runtime identity.
    /// </summary>
    SessionMismatch,

    /// <summary>
    /// The canonical session is playing a different item or play session.
    /// </summary>
    PlaybackMismatch
}

/// <summary>
/// Returns the classified outcome and the server-confirmed play-session identity.
/// </summary>
public sealed class ProfileSwitchSessionStopResult
{
    /// <summary>
    /// Gets or sets the classified compare-and-stop outcome.
    /// </summary>
    public ProfileSwitchSessionStopOutcome Outcome { get; set; }

    /// <summary>
    /// Gets or sets the exact play-session identifier stopped by the server.
    /// </summary>
    public string? PlaySessionId { get; set; }
}
