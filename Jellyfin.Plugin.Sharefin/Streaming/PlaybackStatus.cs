namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// How a start request ended.
/// </summary>
public enum PlaybackStatus
{
    /// <summary>The playback runs.</summary>
    Started,

    /// <summary>The link has no slot left for this viewer.</summary>
    Busy,

    /// <summary>The quality or audio track does not exist.</summary>
    Invalid,
}
