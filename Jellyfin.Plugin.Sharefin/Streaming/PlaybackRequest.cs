#pragma warning disable SA1402, SA1649 // what SharePlayback.StartAsync takes and returns, kept together

namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// What the viewer asks for.
/// </summary>
/// <param name="StartSeconds">Start position in seconds.</param>
/// <param name="AudioIndex">Audio stream index, or null for the default one.</param>
/// <param name="CopyVideo">Whether the browser plays the source video codec. Ignored when a lower quality is asked.</param>
/// <param name="CopyAudio">Whether the browser plays the source audio codec.</param>
/// <param name="Height">Maximum height for a lower quality (720 or 480), or null for the best one.</param>
/// <param name="UserAgent">Client user agent.</param>
public sealed record PlaybackRequest(double StartSeconds, int? AudioIndex, bool CopyVideo, bool CopyAudio, int? Height, string? UserAgent);

/// <summary>
/// The outcome of <see cref="SharePlayback.StartAsync"/>.
/// </summary>
/// <param name="Status">How it ended.</param>
/// <param name="Playback">The playback, when <see cref="PlaybackStatus.Started"/>.</param>
public sealed record PlaybackStart(PlaybackStatus Status, ActivePlayback? Playback = null);
