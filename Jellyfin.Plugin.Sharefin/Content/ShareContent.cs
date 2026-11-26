using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sharefin.Configuration;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.Sharefin.Content;

/// <summary>
/// What the share page shows of an item the gate already let through: its details, seasons and
/// episodes, playback info (tracks, segments, chapters, trickplay), images and the downloadable file.
/// Never a file path, except to the controller serving that file.
/// </summary>
public sealed class ShareContent
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IMediaSegmentManager _segmentManager;
    private readonly IChapterManager _chapterManager;
    private readonly ITrickplayManager _trickplayManager;
    private readonly Func<PluginConfiguration> _config;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareContent"/> class.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="segmentManager">The media segments (intro, recap).</param>
    /// <param name="chapterManager">The chapters.</param>
    /// <param name="trickplayManager">The trickplay thumbnails.</param>
    public ShareContent(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaSegmentManager segmentManager,
        IChapterManager chapterManager,
        ITrickplayManager trickplayManager)
        : this(libraryManager, mediaSourceManager, segmentManager, chapterManager, trickplayManager, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal ShareContent(
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IMediaSegmentManager segmentManager,
        IChapterManager chapterManager,
        ITrickplayManager trickplayManager,
        Func<PluginConfiguration> config)
    {
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _segmentManager = segmentManager;
        _chapterManager = chapterManager;
        _trickplayManager = trickplayManager;
        _config = config;
    }

    /// <summary>
    /// Gets a value indicating whether visitors may get the original file: direct play and the download button.
    /// Without it, every playback goes through ffmpeg.
    /// </summary>
    public bool AllowDownload => _config().AllowDownload;

    /// <summary>
    /// Gets the shared item and, for a series or season, its seasons and their episodes.
    /// </summary>
    /// <param name="root">The shared item.</param>
    /// <returns>The content tree.</returns>
    public static object Describe(BaseItem root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var seasons = root switch
        {
            Series series => series.GetSeasons(null!, new DtoOptions(false))
                .OfType<Season>()
                .OrderBy(s => s.IndexNumber ?? int.MaxValue)
                .Select(s => SeasonDto(s, series))
                .Where(s => s.Episodes.Count > 0)
                .ToList(),
            Season season => [SeasonDto(season, season.Series)],
            _ => null,
        };

        return new { Item = ItemDto(root), Seasons = seasons };
    }

    /// <summary>
    /// Gets the item's image of the given type; a season or episode borrows its series' backdrop and logo.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="type">Primary, Backdrop or Logo.</param>
    /// <returns>The item holding the image and the image, or null.</returns>
    public static (BaseItem Item, ItemImageInfo Info)? FindImage(BaseItem item, ImageType type)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (type == ImageType.Primary)
        {
            var info = item.GetImageInfo(ImageType.Primary, 0) ?? item.GetImageInfo(ImageType.Thumb, 0);
            return info is null ? null : (item, info);
        }

        if (type is not (ImageType.Backdrop or ImageType.Logo))
        {
            return null;
        }

        BaseItem? series = item switch { Episode e => e.Series, Season s => s.Series, _ => null };
        foreach (var candidate in new[] { item, series })
        {
            if (candidate?.GetImageInfo(type, 0) is { } info)
            {
                return (candidate, info);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the video's local file source.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The source, or null when the item is no video or has no local file.</returns>
    public MediaSourceInfo? LocalSource(BaseItem item)
    {
        if (item is not Video)
        {
            return null;
        }

        // shortcut: local files only (no .strm, live TV or remote sources); add protocol handling if needed.
        return _mediaSourceManager.GetStaticMediaSources(item, false)
            .FirstOrDefault(s => s.Protocol == MediaProtocol.File && !string.IsNullOrEmpty(s.Path));
    }

    /// <summary>
    /// Gets what the browser needs to choose between direct play and transcoding. No file path,
    /// and no direct type without <see cref="AllowDownload"/>.
    /// </summary>
    /// <param name="item">The video.</param>
    /// <param name="source">Its local source.</param>
    /// <param name="streamsBusy">Whether the link has no stream slot left for this viewer.</param>
    /// <returns>Container, codecs, audio and text subtitle tracks, intro and recap segments, chapters and trickplay thumbnails.</returns>
    public async Task<object> PlaybackAsync(BaseItem item, MediaSourceInfo source, bool streamsBusy)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(source);
        var streams = source.MediaStreams ?? [];
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var defaultAudio = source.DefaultAudioStreamIndex ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio)?.Index;
        var download = AllowDownload;
        var manifest = await _trickplayManager.GetTrickplayManifest(item).ConfigureAwait(false);
        var resolutions = (manifest.TryGetValue(source.Id, out var own) ? own : manifest.Values.FirstOrDefault())?.Values;
        // A hover tooltip: the smallest resolution at least 240 px wide, else the largest there is.
        var trickplay = resolutions?.Where(r => r.Width >= 240).MinBy(r => r.Width) ?? resolutions?.MaxBy(r => r.Width);
        var segments = await _segmentManager.GetSegmentsAsync(item, [MediaSegmentType.Intro, MediaSegmentType.Recap], _libraryManager.GetLibraryOptions(item), true).ConfigureAwait(false);
        return new
        {
            Name = item.Name,
            DurationSeconds = (source.RunTimeTicks ?? item.RunTimeTicks ?? 0) / (double)TicksPerSecond,
            DirectMime = download ? Mime(source.Path) : null,
            Download = download,
            VideoCodec = video?.Codec,
            DefaultAudioIndex = defaultAudio,
            Audio = streams.Where(s => s.Type == MediaStreamType.Audio)
                .Select(s => new { s.Index, s.Codec, s.Channels, Title = s.DisplayTitle ?? s.Language ?? s.Codec }),
            Subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
                .Select(s => new { s.Index, s.Language, Title = s.DisplayTitle ?? s.Language ?? s.Index.ToString(CultureInfo.InvariantCulture), s.IsDefault }),
            Segments = segments.Select(s => new { Type = s.Type.ToString(), Start = s.StartTicks / (double)TicksPerSecond, End = s.EndTicks / (double)TicksPerSecond }),
            Chapters = _chapterManager.GetChapters(item.Id).Select(c => new { c.Name, Start = c.StartPositionTicks / (double)TicksPerSecond }),
            Trickplay = trickplay is null ? null : new { trickplay.Width, trickplay.Height, trickplay.TileWidth, trickplay.TileHeight, trickplay.ThumbnailCount, trickplay.Interval },
            StreamsBusy = streamsBusy,
        };
    }

    /// <summary>
    /// Gets the original file of a video, for direct play and the download button.
    /// </summary>
    /// <param name="item">The video.</param>
    /// <returns>The path, type and download name, or null without <see cref="AllowDownload"/> or a local file.</returns>
    public (string Path, string Mime, string Name)? DownloadFile(BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var path = AllowDownload ? LocalSource(item)?.Path : null;
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        return (path, Mime(path) ?? "application/octet-stream", DownloadName(item, path));
    }

    /// <summary>
    /// Gets the path of one trickplay tile (a grid of thumbnails) generated by Jellyfin.
    /// </summary>
    /// <param name="item">The video.</param>
    /// <param name="width">Thumbnail width, one of the resolutions in the playback info.</param>
    /// <param name="index">Tile number.</param>
    /// <returns>The JPEG path, or null.</returns>
    public async Task<string?> TrickplayTileAsync(BaseItem item, int width, int index)
    {
        var manifest = await _trickplayManager.GetTrickplayManifest(item).ConfigureAwait(false);
        if (index < 0 || !manifest.Values.Any(r => r.ContainsKey(width)))
        {
            return null;
        }

        var saveWithMedia = _libraryManager.GetLibraryOptions(item).SaveTrickplayWithMedia;
        var path = await _trickplayManager.GetTrickplayTilePathAsync(item, width, index, saveWithMedia).ConfigureAwait(false);
        return string.IsNullOrEmpty(path) || !File.Exists(path) ? null : path;
    }

    internal static string DownloadName(BaseItem item, string path)
    {
        var name = item is Episode e
            ? string.Create(CultureInfo.InvariantCulture, $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00} - {e.Name}")
            : item.ProductionYear is { } year && !item.Name.EndsWith($"({year})", StringComparison.Ordinal) ? string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({year})") : item.Name;
        return name + Path.GetExtension(path);
    }

    internal static string? Mime(string? path) => Path.GetExtension(path)?.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".mov" => "video/mp4",
        ".webm" => "video/webm",
        _ => null,
    };

    private static object ItemDto(BaseItem item) => new
    {
        Id = item.Id.ToString("N"),
        item.Name,
        Type = item.GetBaseItemKind().ToString(),
        item.Overview,
        item.Tagline,
        item.Genres,
        item.OfficialRating,
        item.CommunityRating,
        SeriesName = item is Episode ep ? ep.SeriesName : null,
        Year = item.ProductionYear,
        Season = item is Episode e ? e.ParentIndexNumber : null,
        Episode = item is Episode ? item.IndexNumber : null,
        RuntimeMinutes = item.RunTimeTicks is { } ticks ? (int?)(ticks / TimeSpan.TicksPerMinute) : null,
        HasImage = item.HasImage(ImageType.Primary) || item.HasImage(ImageType.Thumb),
        HasBackdrop = FindImage(item, ImageType.Backdrop) is not null,
        HasLogo = FindImage(item, ImageType.Logo) is not null,
        Playable = item is Video,
    };

    private static SeasonView SeasonDto(Season season, Series? series)
    {
        var episodes = season.GetEpisodes(series, null!, new DtoOptions(false), false)
            .OfType<Episode>()
            .Where(e => !e.IsVirtualItem)
            .Select(ItemDto)
            .ToList();
        return new SeasonView(season.Id.ToString("N"), season.Name, season.IndexNumber, episodes);
    }

    private sealed record SeasonView(string Id, string Name, int? Index, List<object> Episodes);
}
