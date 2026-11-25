using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sharefin.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// Starts viewers' playbacks through ffmpeg: at most <see cref="PluginConfiguration.MaxStreamsPerLink"/>
/// viewers per link, one job per viewer (a seek stops the previous one), and the slot always freed
/// when the playback is disposed or fails to start.
/// </summary>
public sealed class SharePlayback
{
    private const int DefaultHeight = 1080;

    private readonly IPlaybackJobs _jobs;
    private readonly Func<PluginConfiguration> _config;
    private readonly StreamLimiter _slots = new();

    internal SharePlayback(IPlaybackJobs jobs, Func<PluginConfiguration> config)
    {
        _jobs = jobs;
        _config = config;
    }

    private int MaxStreams => _config().MaxStreamsPerLink;

    /// <summary>
    /// Whether the link has no slot left for this viewer.
    /// </summary>
    /// <param name="viewer">The viewer.</param>
    /// <returns>True when a start would be refused.</returns>
    public bool IsBusy(Viewer viewer)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        return _slots.IsFull(viewer.LinkId, viewer.Id, MaxStreams);
    }

    /// <summary>
    /// Takes the viewer's slot and starts a remuxed or transcoded fragmented MP4.
    /// The caller must dispose the playback when the response ends.
    /// </summary>
    /// <param name="viewer">The viewer.</param>
    /// <param name="item">The video.</param>
    /// <param name="source">Its local media source.</param>
    /// <param name="request">What the viewer asks for.</param>
    /// <returns>The playback, or why there is none. An ffmpeg failure throws, with the slot already freed.</returns>
    public async Task<PlaybackStart> StartAsync(Viewer viewer, BaseItem item, MediaSourceInfo source, PlaybackRequest request)
    {
        ArgumentNullException.ThrowIfNull(viewer);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        if ((request.Height is { } height && !ShareTranscoder.Bitrates.ContainsKey(height))
            || (request.AudioIndex is { } audio && source.MediaStreams?.Any(s => s.Type == MediaStreamType.Audio && s.Index == audio) != true))
        {
            return new PlaybackStart(PlaybackStatus.Invalid);
        }

        if (!_slots.TryEnter(viewer.LinkId, viewer.Id, MaxStreams))
        {
            return new PlaybackStart(PlaybackStatus.Busy);
        }

        var playSessionId = Guid.NewGuid().ToString("N");
        try
        {
            await _jobs.StopViewerAsync(viewer.Id).ConfigureAwait(false);
            var stream = await _jobs.StartAsync(new PlaybackJob(
                item,
                source,
                viewer.Id,
                playSessionId,
                (long)(Math.Clamp(request.StartSeconds, 0, 7 * 24 * 3600) * TimeSpan.TicksPerSecond),
                request.AudioIndex,
                request.Height ?? DefaultHeight,
                request.CopyVideo && request.Height is null,
                request.CopyAudio,
                request.UserAgent)).ConfigureAwait(false);
            return new PlaybackStart(PlaybackStatus.Started, new ActivePlayback(stream, () => ReleaseAsync(viewer, playSessionId)));
        }
        catch
        {
            await ReleaseAsync(viewer, playSessionId).ConfigureAwait(false);
            throw;
        }
    }

    private Task ReleaseAsync(Viewer viewer, string playSessionId)
    {
        _slots.Leave(viewer.LinkId, viewer.Id);
        return _jobs.StopSessionAsync(playSessionId);
    }
}
