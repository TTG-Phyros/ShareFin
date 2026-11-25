using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// Starts a progressive fragmented-MP4 ffmpeg job through Jellyfin's own transcode manager,
/// without any user: the job runs with an empty user id, which skips per-user permission checks.
/// Video and audio are either copied (remux) or encoded to H.264/AAC, as the browser says it needs.
/// </summary>
internal sealed class ShareTranscoder : IPlaybackJobs
{
    private const int MaxAudioChannels = 2;

    /// <summary>
    /// Qualities the viewer can pick: maximum height to maximum video bitrate. 1080 is the default.
    /// </summary>
    internal static readonly IReadOnlyDictionary<int, int> Bitrates = new Dictionary<int, int>
    {
        [1080] = 20_000_000,
        [720] = 4_000_000,
        [480] = 1_500_000,
    };

    // Jellyfin checks a marker file in the transcode folder on each GetTranscodePath call:
    // two streams starting at the same moment failed on it with a sharing violation (500).
    private static readonly Lock TranscodePathGate = new();

    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly ITranscodeManager _transcodeManager;
    private readonly EncodingHelper _encodingHelper;
    private readonly IServerConfigurationManager _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareTranscoder"/> class.
    /// </summary>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="transcodeManager">The transcode manager.</param>
    /// <param name="encodingHelper">Jellyfin's ffmpeg command line builder.</param>
    /// <param name="config">The server configuration.</param>
    public ShareTranscoder(IMediaSourceManager mediaSourceManager, ITranscodeManager transcodeManager, EncodingHelper encodingHelper, IServerConfigurationManager config)
    {
        _mediaSourceManager = mediaSourceManager;
        _transcodeManager = transcodeManager;
        _encodingHelper = encodingHelper;
        _config = config;
    }

    /// <inheritdoc />
    public async Task<Stream> StartAsync(PlaybackJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var (item, source, deviceId, playSessionId, startTicks, audioStreamIndex, maxHeight, copyVideo, copyAudio, userAgent) = job;

        var request = new VideoRequestDto
        {
            Id = item.Id,
            MediaSourceId = source.Id,
            DeviceId = deviceId,
            PlaySessionId = playSessionId,
            Container = "mp4",
            VideoCodec = "h264",
            AudioCodec = "aac",
            StartTimeTicks = startTicks > 0 ? startTicks : null,
            AudioStreamIndex = audioStreamIndex,
            MaxWidth = ((maxHeight * 16) + 8) / 9, // rounded up (854 for 480): rounding down made Jellyfin output 852x478
            MaxHeight = maxHeight,
            VideoBitRate = Bitrates[maxHeight],
            MaxAudioChannels = MaxAudioChannels,
            Context = EncodingContext.Streaming,
        };

        var options = _config.GetEncodingOptions();
        var state = new StreamState(_mediaSourceManager, TranscodingJobType.Progressive, _transcodeManager)
        {
            Request = request,
            RequestedUrl = "stream.mp4",
            UserAgent = userAgent,
            IsInputVideo = true,
        };

        try
        {
            _encodingHelper.AttachMediaSourceInfo(state, options, source, state.RequestedUrl);
            state.SupportedVideoCodecs = ["h264"];
            state.SupportedAudioCodecs = ["aac"];
            state.OutputContainer = "mp4";
            state.OutputVideoCodec = copyVideo ? "copy" : "h264";
            state.OutputAudioCodec = copyAudio ? "copy" : "aac";
            state.OutputAudioChannels = _encodingHelper.GetNumAudioChannelsParam(state, state.AudioStream, state.OutputAudioCodec);
            state.OutputAudioBitrate = _encodingHelper.GetAudioBitrateParam(request.AudioBitRate, "aac", state.AudioStream, state.OutputAudioChannels) ?? 0;
            state.OutputVideoBitrate = _encodingHelper.GetVideoBitrateParamValue(request, state.VideoStream, "h264");

            // Unique file per request, so a seek never picks up the output of an earlier position.
            string transcodePath;
            lock (TranscodePathGate)
            {
                transcodePath = _config.GetTranscodePath();
            }

            state.OutputFilePath = Path.Combine(transcodePath, "sharefin-" + playSessionId + ".mp4");
            var args = _encodingHelper.GetProgressiveVideoFullCommandLine(state, options, EncoderPreset.superfast);

#pragma warning disable CA2000 // The transcoding job owns and disposes this source, as in Jellyfin's own controllers.
            var ffmpeg = await _transcodeManager.StartFfMpeg(state, state.OutputFilePath, args, Guid.Empty, TranscodingJobType.Progressive, new CancellationTokenSource()).ConfigureAwait(false);
#pragma warning restore CA2000
            return new ProgressiveFileStream(state.OutputFilePath, ffmpeg, _transcodeManager);
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopViewerAsync(string viewerId) => _transcodeManager.KillTranscodingJobs(viewerId, null, _ => true);

    /// <summary>
    /// Stops a job if still running and deletes its output file. Jellyfin only deletes the file
    /// when it kills a job, never when ffmpeg finishes on its own, so this runs after every response.
    /// </summary>
    /// <param name="playSessionId">The job id.</param>
    /// <returns>A task.</returns>
    public Task StopSessionAsync(string playSessionId) => _transcodeManager.KillTranscodingJobs(string.Empty, playSessionId, _ => true);
}
