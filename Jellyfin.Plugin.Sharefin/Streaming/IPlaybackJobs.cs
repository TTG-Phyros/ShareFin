#pragma warning disable SA1402, SA1649 // the port and the job it takes, kept together

using System.IO;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// The seam between <see cref="SharePlayback"/> and ffmpeg: <see cref="ShareTranscoder"/> in Jellyfin, a fake in tests.
/// </summary>
internal interface IPlaybackJobs
{
    /// <summary>
    /// Starts an ffmpeg job.
    /// </summary>
    /// <param name="job">What to play and how.</param>
    /// <returns>A stream following the growing output file.</returns>
    Task<Stream> StartAsync(PlaybackJob job);

    /// <summary>
    /// Stops every job of a viewer.
    /// </summary>
    /// <param name="viewerId">The viewer id the jobs were started with.</param>
    /// <returns>A task.</returns>
    Task StopViewerAsync(string viewerId);

    /// <summary>
    /// Stops one job if still running and deletes its output.
    /// </summary>
    /// <param name="playSessionId">The job id.</param>
    /// <returns>A task.</returns>
    Task StopSessionAsync(string playSessionId);
}

/// <summary>
/// One ffmpeg job.
/// </summary>
/// <param name="Item">The video to play.</param>
/// <param name="Source">Its local media source.</param>
/// <param name="ViewerId">The viewer, used as Jellyfin's device id.</param>
/// <param name="PlaySessionId">Unique id of this job.</param>
/// <param name="StartTicks">Start position.</param>
/// <param name="AudioStreamIndex">Audio stream to use, or null for the default one.</param>
/// <param name="MaxHeight">Maximum height, a key of <see cref="ShareTranscoder.Bitrates"/>; only applies when the video is encoded.</param>
/// <param name="CopyVideo">Whether the source video is copied rather than encoded.</param>
/// <param name="CopyAudio">Whether the source audio is copied rather than encoded.</param>
/// <param name="UserAgent">Client user agent, for Jellyfin's job bookkeeping.</param>
internal sealed record PlaybackJob(
    BaseItem Item,
    MediaSourceInfo Source,
    string ViewerId,
    string PlaySessionId,
    long StartTicks,
    int? AudioStreamIndex,
    int MaxHeight,
    bool CopyVideo,
    bool CopyAudio,
    string? UserAgent);
