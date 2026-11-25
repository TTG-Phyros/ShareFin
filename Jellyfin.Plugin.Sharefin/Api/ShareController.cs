using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using Jellyfin.Plugin.Sharefin.Streaming;
using MediaBrowser.Controller.Chapters;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.MediaSegments;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Branding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sharefin.Api;

/// <summary>
/// Public endpoints for share link recipients. No Jellyfin account or API key is involved:
/// every request is checked against the link (exists, active), the per-link cookie
/// (issued for the current password/revocation stamp) and the shared item tree.
/// All routes live under /Sharefin/s/{token} so the cookie path covers them.
/// </summary>
[ApiController]
[Route("Sharefin/s/{token}")]
[AllowAnonymous]
public class ShareController : ControllerBase
{
    private const long TicksPerSecond = TimeSpan.TicksPerSecond;

    private static readonly Lazy<string> PageTemplate = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Sharefin.Web.share.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly ShareLinkStore _store;
    private readonly ShareCookie _cookie;
    private readonly LoginThrottle _throttle;
    private readonly SharePlayback _playback;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly IMediaSegmentManager _segmentManager;
    private readonly IChapterManager _chapterManager;
    private readonly ITrickplayManager _trickplayManager;
    private readonly ILogger<ShareController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareController"/> class.
    /// </summary>
    /// <param name="store">The link store.</param>
    /// <param name="cookie">The access cookie service.</param>
    /// <param name="throttle">The password attempt limiter.</param>
    /// <param name="playback">The playbacks through ffmpeg.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="subtitleEncoder">The subtitle encoder.</param>
    /// <param name="configurationManager">The server configuration, for the custom CSS theme.</param>
    /// <param name="segmentManager">The media segments (intro, recap).</param>
    /// <param name="chapterManager">The chapters.</param>
    /// <param name="trickplayManager">The trickplay thumbnails.</param>
    /// <param name="logger">The logger.</param>
    public ShareController(
        ShareLinkStore store,
        ShareCookie cookie,
        LoginThrottle throttle,
        SharePlayback playback,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IImageProcessor imageProcessor,
        ISubtitleEncoder subtitleEncoder,
        IServerConfigurationManager configurationManager,
        IMediaSegmentManager segmentManager,
        IChapterManager chapterManager,
        ITrickplayManager trickplayManager,
        ILogger<ShareController> logger)
    {
        _store = store;
        _cookie = cookie;
        _throttle = throttle;
        _playback = playback;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _imageProcessor = imageProcessor;
        _subtitleEncoder = subtitleEncoder;
        _configurationManager = configurationManager;
        _segmentManager = segmentManager;
        _chapterManager = chapterManager;
        _trickplayManager = trickplayManager;
        _logger = logger;
    }

    private static PluginConfigurationSnapshot Settings
    {
        get
        {
            var c = Plugin.Instance?.Configuration;
            return new PluginConfigurationSnapshot(c?.RememberDays ?? 30, c?.MaxFailuresPerLink ?? 5, c?.MaxFailuresPerIp ?? 20, c?.AllowDownload ?? true);
        }
    }

    private string ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>
    /// Serves the share page. The page itself asks for the password or shows the content.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <returns>The HTML page, or 404.</returns>
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Page([FromRoute] string token)
    {
        NoStore();
        Response.Headers["Referrer-Policy"] = "no-referrer"; // the token is in the URL
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (ActiveLink(token) is null)
        {
            return NotFound();
        }

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        // Jellyfin's custom CSS (Dashboard → General), e.g. an ElegantFin @import: the page reuses its variables and font.
        var themed = !string.IsNullOrWhiteSpace((_configurationManager.GetConfiguration("branding") as BrandingOptions)?.CustomCss);
        var external = themed ? " 'self' https:" : string.Empty; // the theme's own @imports, fonts and images
        Response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'{external}; font-src 'self' data:{external}; img-src 'self' data:{external}; media-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        var theme = themed ? $"<style>@import url(\"{Request.PathBase.ToUriComponent()}/Branding/Css.css\") layer(theme);</style>" : string.Empty;
        var page = PageTemplate.Value
            .Replace("{{nonce}}", nonce, StringComparison.Ordinal)
            .Replace("{{theme}}", theme, StringComparison.Ordinal);
        return Content(page, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Checks the password and, if right, sets the per-link access cookie.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="request">The password.</param>
    /// <returns>204 when accepted, 401 when wrong, 429 when locked, 404 when the link is unusable.</returns>
    [HttpPost("auth")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult Auth([FromRoute] string token, [FromBody] AuthRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        NoStore();
        var link = ActiveLink(token);
        if (link is null)
        {
            return NotFound();
        }

        var ip = ClientIp;
        if (_throttle.RetryAfter(link.Token, ip) is { } wait)
        {
            return TooMany(wait);
        }

        if (!SharePassword.Verify(request.Password, link.PasswordHash))
        {
            var settings = Settings;
            _throttle.Fail(link.Token, ip, settings.MaxFailuresPerLink, settings.MaxFailuresPerIp);
            _logger.LogInformation("Sharefin: wrong password for link {LinkId} from {Ip}", link.Id, ip);
            return _throttle.RetryAfter(link.Token, ip) is { } lockWait ? TooMany(lockWait) : Unauthorized();
        }

        _throttle.Succeed(link.Token);
        IssueCookie(link);
        return NoContent();
    }

    /// <summary>
    /// Gets the shared content: the item, and its seasons and episodes for a series or season.
    /// Counts one access per browser session (not per reload, which would rewrite the store each time)
    /// and, for a permanent link, renews the remembered password.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <returns>The content tree.</returns>
    [HttpGet("content")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetContent([FromRoute] string token)
    {
        NoStore();
        if (!TryAuthorize(token, null, out var access, out var error))
        {
            return error;
        }

        var visit = ShareCookie.CookieName(access.Link.Token) + "v";
        if (!Request.Cookies.ContainsKey(visit))
        {
            _store.Update(access.Link.Id, l =>
            {
                l.AccessCount++;
                l.LastAccessUtc = DateTime.UtcNow;
            });
            Response.Cookies.Append(visit, "1", CookieOptionsFor(access.Link, null)); // no expiry: gone when the browser closes
        }

        if (access.Link.ExpiresUtc is null)
        {
            IssueCookie(access.Link);
        }

        var root = access.Root;
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

        return Ok(new
        {
            Item = ItemDto(root),
            Seasons = seasons,
        });
    }

    /// <summary>
    /// Gets what the browser needs to choose between direct play and transcoding.
    /// No file path is returned. Without <see cref="Configuration.PluginConfiguration.AllowDownload"/>,
    /// no direct type is given either, so the browser always goes through ffmpeg.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The video.</param>
    /// <param name="sid">The viewer's random session id, see <see cref="Viewer.TryCreate"/>.</param>
    /// <returns>Container, codecs, audio and text subtitle tracks, intro and recap segments, chapters, trickplay thumbnails, and whether the link has no stream slot left for this viewer.</returns>
    [HttpGet("items/{itemId}/playback")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Playback([FromRoute] string token, [FromRoute] Guid itemId, [FromQuery] string? sid = null)
    {
        NoStore();
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var viewer = Viewer.TryCreate(access.Link, ClientIp, sid);
        if (viewer is null)
        {
            return BadRequest();
        }

        var source = LocalSource(access.Item);
        if (source is null)
        {
            return NotFound();
        }

        var streams = source.MediaStreams ?? [];
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var defaultAudio = source.DefaultAudioStreamIndex ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio)?.Index;
        var download = Settings.AllowDownload;
        var manifest = await _trickplayManager.GetTrickplayManifest(access.Item).ConfigureAwait(false);
        var resolutions = (manifest.TryGetValue(source.Id, out var own) ? own : manifest.Values.FirstOrDefault())?.Values;
        // A hover tooltip: the smallest resolution at least 240 px wide, else the largest there is.
        var trickplay = resolutions?.Where(r => r.Width >= 240).MinBy(r => r.Width) ?? resolutions?.MaxBy(r => r.Width);
        var segments = await _segmentManager.GetSegmentsAsync(access.Item, [MediaSegmentType.Intro, MediaSegmentType.Recap], _libraryManager.GetLibraryOptions(access.Item), true).ConfigureAwait(false);
        return Ok(new
        {
            Name = access.Item.Name,
            DurationSeconds = (source.RunTimeTicks ?? access.Item.RunTimeTicks ?? 0) / (double)TicksPerSecond,
            DirectMime = download ? DirectMime(source.Path) : null,
            Download = download,
            VideoCodec = video?.Codec,
            DefaultAudioIndex = defaultAudio,
            Audio = streams.Where(s => s.Type == MediaStreamType.Audio)
                .Select(s => new { s.Index, s.Codec, s.Channels, Title = s.DisplayTitle ?? s.Language ?? s.Codec }),
            Subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
                .Select(s => new { s.Index, s.Language, Title = s.DisplayTitle ?? s.Language ?? s.Index.ToString(CultureInfo.InvariantCulture), s.IsDefault }),
            Segments = segments.Select(s => new { Type = s.Type.ToString(), Start = s.StartTicks / (double)TicksPerSecond, End = s.EndTicks / (double)TicksPerSecond }),
            Chapters = _chapterManager.GetChapters(access.Item.Id).Select(c => new { c.Name, Start = c.StartPositionTicks / (double)TicksPerSecond }),
            Trickplay = trickplay is null ? null : new { trickplay.Width, trickplay.Height, trickplay.TileWidth, trickplay.TileHeight, trickplay.ThumbnailCount, trickplay.Interval },
            StreamsBusy = _playback.IsBusy(viewer),
        });
    }

    /// <summary>
    /// Streams the original file, with HTTP range support, for direct play and the download button.
    /// Not found unless <see cref="Configuration.PluginConfiguration.AllowDownload"/>.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The video.</param>
    /// <returns>The file.</returns>
    [HttpGet("items/{itemId}/stream")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Stream([FromRoute] string token, [FromRoute] Guid itemId)
    {
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var source = LocalSource(access.Item);
        if (!Settings.AllowDownload || source is null || !System.IO.File.Exists(source.Path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store";
        // The attachment name only matters to the download button: a <video> ignores Content-Disposition.
        return PhysicalFile(source.Path, DirectMime(source.Path) ?? "application/octet-stream", DownloadName(access.Item, source.Path), enableRangeProcessing: true);
    }

    /// <summary>
    /// Streams a remuxed or transcoded fragmented MP4, starting at a given position.
    /// One job per link and viewer (browser tab): a new request (a seek) stops the previous job.
    /// At most <see cref="Configuration.PluginConfiguration.MaxStreamsPerLink"/> viewers per link.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The video.</param>
    /// <param name="t">Start position in seconds.</param>
    /// <param name="audio">Audio stream index.</param>
    /// <param name="vcopy">Whether the browser plays the source video codec. Ignored when a lower quality is asked.</param>
    /// <param name="acopy">Whether the browser plays the source audio codec.</param>
    /// <param name="sid">Random id the page draws once, so viewers behind one address do not stop each other's stream.</param>
    /// <param name="height">Maximum height for a lower quality (720 or 480), or null for the best one.</param>
    /// <returns>The stream, or 429 when the link already has too many viewers.</returns>
    [HttpGet("items/{itemId}/transcode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult> Transcode(
        [FromRoute] string token,
        [FromRoute] Guid itemId,
        [FromQuery] double t = 0,
        [FromQuery] int? audio = null,
        [FromQuery] bool vcopy = false,
        [FromQuery] bool acopy = false,
        [FromQuery] string? sid = null,
        [FromQuery] int? height = null)
    {
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var source = LocalSource(access.Item);
        if (source is null)
        {
            return NotFound();
        }

        var viewer = Viewer.TryCreate(access.Link, ClientIp, sid);
        if (viewer is null)
        {
            return BadRequest();
        }

        var start = await _playback.StartAsync(viewer, access.Item, source, new PlaybackRequest(t, audio, vcopy, acopy, height, Request.Headers.UserAgent.ToString())).ConfigureAwait(false);
        switch (start.Status)
        {
            case PlaybackStatus.Invalid:
                return BadRequest();
            case PlaybackStatus.Busy:
                _logger.LogInformation("Sharefin: stream refused for link {LinkId}, too many viewers", access.Link.Id);
                return StatusCode(StatusCodes.Status429TooManyRequests);
        }

        // Disposed when the request ends, including when the browser disconnects: frees the slot and stops ffmpeg.
        Response.RegisterForDisposeAsync(start.Playback!);
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.AcceptRanges = "none";
        return File(start.Playback!.Stream, "video/mp4");
    }

    /// <summary>
    /// Gets a text subtitle track as WebVTT, shifted to start at <paramref name="t"/> for transcoded streams.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The video.</param>
    /// <param name="index">The subtitle stream index.</param>
    /// <param name="t">Position in seconds the video stream starts at.</param>
    /// <returns>The WebVTT file.</returns>
    [HttpGet("items/{itemId}/subtitles/{index}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Subtitles([FromRoute] string token, [FromRoute] Guid itemId, [FromRoute] int index, [FromQuery] double t = 0)
    {
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var source = LocalSource(access.Item);
        if (source?.MediaStreams?.Any(s => s.Index == index && s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream) != true)
        {
            return NotFound();
        }

        var startTicks = (long)(Math.Max(0, t) * TicksPerSecond);
        var vtt = await _subtitleEncoder.GetSubtitles(access.Item, source.Id, index, "vtt", startTicks, 0, false, HttpContext.RequestAborted).ConfigureAwait(false);
        NoStore();
        return File(vtt, "text/vtt; charset=utf-8");
    }

    /// <summary>
    /// Gets one trickplay tile image (a grid of thumbnails) generated by Jellyfin, for the timeline tooltip.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The video.</param>
    /// <param name="width">Thumbnail width, one of the resolutions in the playback info.</param>
    /// <param name="index">Tile number.</param>
    /// <returns>The JPEG tile.</returns>
    [HttpGet("items/{itemId}/trickplay/{width}/{index}.jpg")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Trickplay([FromRoute] string token, [FromRoute] Guid itemId, [FromRoute] int width, [FromRoute] int index)
    {
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var manifest = await _trickplayManager.GetTrickplayManifest(access.Item).ConfigureAwait(false);
        if (index < 0 || !manifest.Values.Any(r => r.ContainsKey(width)))
        {
            return NotFound();
        }

        var saveWithMedia = _libraryManager.GetLibraryOptions(access.Item).SaveTrickplayWithMedia;
        var path = await _trickplayManager.GetTrickplayTilePathAsync(access.Item, width, index, saveWithMedia).ConfigureAwait(false);
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, "image/jpeg");
    }

    /// <summary>
    /// Gets an item's primary, backdrop or logo image, resized. Backdrops and logos
    /// fall back to the parent series' for a season or episode.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="maxWidth">Maximum width in pixels.</param>
    /// <param name="type">Primary, Backdrop or Logo.</param>
    /// <returns>The image.</returns>
    [HttpGet("items/{itemId}/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Image([FromRoute] string token, [FromRoute] Guid itemId, [FromQuery] int maxWidth = 400, [FromQuery] ImageType type = ImageType.Primary)
    {
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var found = FindImage(access.Item, type);
        if (found is null || !found.Value.Info.IsLocalFile)
        {
            return NotFound();
        }

        var (item, info) = found.Value;
        var (path, mime, _) = await _imageProcessor.ProcessImage(new ImageProcessingOptions
        {
            Item = item,
            ItemId = item.Id,
            Image = info,
            MaxWidth = Math.Clamp(maxWidth, 64, 1920),
            Quality = 90,
            SupportedOutputFormats = [ImageFormat.Webp, ImageFormat.Jpg],
        }).ConfigureAwait(false);
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, mime ?? "image/jpeg");
    }

    private static string DownloadName(BaseItem item, string path)
    {
        var name = item is Episode e
            ? string.Create(CultureInfo.InvariantCulture, $"{e.SeriesName} S{e.ParentIndexNumber ?? 0:00}E{e.IndexNumber ?? 0:00} - {e.Name}")
            : item.ProductionYear is { } year && !item.Name.EndsWith($"({year})", StringComparison.Ordinal) ? string.Create(CultureInfo.InvariantCulture, $"{item.Name} ({year})") : item.Name;
        return name + Path.GetExtension(path);
    }

    private static string? DirectMime(string? path) => Path.GetExtension(path)?.ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".mov" => "video/mp4",
        ".webm" => "video/webm",
        _ => null,
    };

    /// <summary>
    /// The item's image of the given type; a season or episode borrows its series' backdrop and logo.
    /// </summary>
    private static (BaseItem Item, ItemImageInfo Info)? FindImage(BaseItem item, ImageType type)
    {
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

    private ShareLink? ActiveLink(string token)
    {
        var link = _store.GetByToken(token);
        return link is not null && link.IsActive(DateTime.UtcNow) ? link : null;
    }

    /// <summary>
    /// The single access check used by every content and media endpoint.
    /// </summary>
    private bool TryAuthorize(string token, Guid? itemId, out ShareAccess access, out ActionResult error)
    {
        access = default!;
        error = NotFound();
        var link = ActiveLink(token);
        if (link is null)
        {
            return false;
        }

        if (!_cookie.Validate(Request.Cookies[ShareCookie.CookieName(link.Token)], link))
        {
            error = Unauthorized();
            return false;
        }

        var root = _libraryManager.GetItemById(link.ItemId);
        if (root is null)
        {
            return false;
        }

        var item = itemId is null || itemId.Value.Equals(root.Id) ? root : _libraryManager.GetItemById(itemId.Value);
        if (item is null || !ShareScope.Contains(root, item))
        {
            return false;
        }

        access = new ShareAccess(link, root, item);
        return true;
    }

    private MediaSourceInfo? LocalSource(BaseItem item)
    {
        if (item is not Video)
        {
            return null;
        }

        // shortcut: local files only (no .strm, live TV or remote sources); add protocol handling if needed.
        return _mediaSourceManager.GetStaticMediaSources(item, false)
            .FirstOrDefault(s => s.Protocol == MediaProtocol.File && !string.IsNullOrEmpty(s.Path));
    }

    private void IssueCookie(ShareLink link)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = ShareCookie.ExpiryFor(link, Settings.RememberDays, now);
        Response.Cookies.Append(ShareCookie.CookieName(link.Token), _cookie.Issue(link, expires, now), CookieOptionsFor(link, expires));
    }

    private CookieOptions CookieOptionsFor(ShareLink link, DateTimeOffset? expires) => new()
    {
        HttpOnly = true,
        Secure = Request.IsHttps, // browsers drop Secure cookies set over plain http, which made login loop
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Path = Request.PathBase + "/Sharefin/s/" + link.Token,
        Expires = expires,
    };

    private ObjectResult TooMany(TimeSpan wait)
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new { RetryAfterSeconds = seconds });
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private sealed record ShareAccess(ShareLink Link, BaseItem Root, BaseItem Item);

    private sealed record SeasonView(string Id, string Name, int? Index, List<object> Episodes);

    private sealed record PluginConfigurationSnapshot(int RememberDays, int MaxFailuresPerLink, int MaxFailuresPerIp, bool AllowDownload);
}
