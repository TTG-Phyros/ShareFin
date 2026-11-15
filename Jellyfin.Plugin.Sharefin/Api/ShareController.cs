using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using Jellyfin.Plugin.Sharefin.Streaming;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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
    private readonly ShareTranscoder _transcoder;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaSourceManager _mediaSourceManager;
    private readonly IImageProcessor _imageProcessor;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly ILogger<ShareController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareController"/> class.
    /// </summary>
    /// <param name="store">The link store.</param>
    /// <param name="cookie">The access cookie service.</param>
    /// <param name="throttle">The password attempt limiter.</param>
    /// <param name="transcoder">The transcoder.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaSourceManager">The media source manager.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="subtitleEncoder">The subtitle encoder.</param>
    /// <param name="logger">The logger.</param>
    public ShareController(
        ShareLinkStore store,
        ShareCookie cookie,
        LoginThrottle throttle,
        ShareTranscoder transcoder,
        ILibraryManager libraryManager,
        IMediaSourceManager mediaSourceManager,
        IImageProcessor imageProcessor,
        ISubtitleEncoder subtitleEncoder,
        ILogger<ShareController> logger)
    {
        _store = store;
        _cookie = cookie;
        _throttle = throttle;
        _transcoder = transcoder;
        _libraryManager = libraryManager;
        _mediaSourceManager = mediaSourceManager;
        _imageProcessor = imageProcessor;
        _subtitleEncoder = subtitleEncoder;
        _logger = logger;
    }

    private static PluginConfigurationSnapshot Settings
    {
        get
        {
            var c = Plugin.Instance?.Configuration;
            return new PluginConfigurationSnapshot(c?.RememberDays ?? 30, c?.MaxFailuresPerLink ?? 5, c?.MaxFailuresPerIp ?? 20);
        }
    }

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
        Response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; img-src 'self'; media-src 'self'; connect-src 'self'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Content(PageTemplate.Value.Replace("{{nonce}}", nonce, StringComparison.Ordinal), "text/html; charset=utf-8");
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

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
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
    /// Counts one access and, for a permanent link, renews the remembered password.
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

        _store.Update(access.Link.Id, l =>
        {
            l.AccessCount++;
            l.LastAccessUtc = DateTime.UtcNow;
        });
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
    /// No file path is returned.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The video.</param>
    /// <returns>Container, codecs, audio and text subtitle tracks.</returns>
    [HttpGet("items/{itemId}/playback")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Playback([FromRoute] string token, [FromRoute] Guid itemId)
    {
        NoStore();
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var source = LocalSource(access.Item);
        if (source is null)
        {
            return NotFound();
        }

        var streams = source.MediaStreams ?? [];
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var defaultAudio = source.DefaultAudioStreamIndex ?? streams.FirstOrDefault(s => s.Type == MediaStreamType.Audio)?.Index;
        return Ok(new
        {
            Name = access.Item.Name,
            DurationSeconds = (source.RunTimeTicks ?? access.Item.RunTimeTicks ?? 0) / (double)TicksPerSecond,
            DirectMime = DirectMime(source.Path),
            VideoCodec = video?.Codec,
            DefaultAudioIndex = defaultAudio,
            Audio = streams.Where(s => s.Type == MediaStreamType.Audio)
                .Select(s => new { s.Index, s.Codec, s.Channels, Title = s.DisplayTitle ?? s.Language ?? s.Codec }),
            Subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream)
                .Select(s => new { s.Index, s.Language, Title = s.DisplayTitle ?? s.Language ?? s.Index.ToString(CultureInfo.InvariantCulture), s.IsDefault }),
        });
    }

    /// <summary>
    /// Streams the original file, with HTTP range support.
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
        if (source is null || !System.IO.File.Exists(source.Path))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store";
        return PhysicalFile(source.Path, DirectMime(source.Path) ?? "application/octet-stream", enableRangeProcessing: true);
    }

    /// <summary>
    /// Streams a remuxed or transcoded fragmented MP4, starting at a given position.
    /// One job per link and client address: a new request (a seek) stops the previous job.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The video.</param>
    /// <param name="t">Start position in seconds.</param>
    /// <param name="audio">Audio stream index.</param>
    /// <param name="vcopy">Whether the browser plays the source video codec.</param>
    /// <param name="acopy">Whether the browser plays the source audio codec.</param>
    /// <returns>The stream.</returns>
    [HttpGet("items/{itemId}/transcode")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Transcode(
        [FromRoute] string token,
        [FromRoute] Guid itemId,
        [FromQuery] double t = 0,
        [FromQuery] int? audio = null,
        [FromQuery] bool vcopy = false,
        [FromQuery] bool acopy = false)
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

        if (audio is not null && source.MediaStreams?.Any(s => s.Type == MediaStreamType.Audio && s.Index == audio) != true)
        {
            return BadRequest();
        }

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var deviceId = "sharefin-" + access.Link.Id.ToString("N") + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ip)).AsSpan(0, 6));
        var startTicks = (long)(Math.Clamp(t, 0, 7 * 24 * 3600) * TicksPerSecond);
        var playSessionId = Guid.NewGuid().ToString("N");
        var stream = await _transcoder.StartAsync(access.Item, source, deviceId, playSessionId, startTicks, audio, vcopy, acopy, Request.Headers.UserAgent.ToString()).ConfigureAwait(false);
        Response.OnCompleted(() => _transcoder.StopAsync(playSessionId)); // also runs when the browser disconnects
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.AcceptRanges = "none";
        return File(stream, "video/mp4");
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
    /// Gets an item's primary image, resized.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="itemId">The item.</param>
    /// <param name="maxWidth">Maximum width in pixels.</param>
    /// <returns>The image.</returns>
    [HttpGet("items/{itemId}/image")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> Image([FromRoute] string token, [FromRoute] Guid itemId, [FromQuery] int maxWidth = 400)
    {
        if (!TryAuthorize(token, itemId, out var access, out var error))
        {
            return error;
        }

        var item = access.Item;
        var info = item.GetImageInfo(ImageType.Primary, 0) ?? item.GetImageInfo(ImageType.Thumb, 0);
        if (info is null || !info.IsLocalFile)
        {
            return NotFound();
        }

        var (path, mime, _) = await _imageProcessor.ProcessImage(new ImageProcessingOptions
        {
            Item = item,
            ItemId = item.Id,
            Image = info,
            MaxWidth = Math.Clamp(maxWidth, 64, 1280),
            Quality = 90,
            SupportedOutputFormats = [ImageFormat.Webp, ImageFormat.Jpg],
        }).ConfigureAwait(false);
        Response.Headers.CacheControl = "private, max-age=3600";
        return PhysicalFile(path, mime ?? "image/jpeg");
    }

    private static string? DirectMime(string? path) => Path.GetExtension(path)?.ToLowerInvariant() switch
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
        Year = item.ProductionYear,
        Season = item is Episode e ? e.ParentIndexNumber : null,
        Episode = item is Episode ? item.IndexNumber : null,
        RuntimeMinutes = item.RunTimeTicks is { } ticks ? (int?)(ticks / TimeSpan.TicksPerMinute) : null,
        HasImage = item.HasImage(ImageType.Primary) || item.HasImage(ImageType.Thumb),
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
        Response.Cookies.Append(ShareCookie.CookieName(link.Token), _cookie.Issue(link, expires, now), new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = Request.PathBase + "/Sharefin/s/" + link.Token,
            Expires = expires,
        });
    }

    private ObjectResult TooMany(TimeSpan wait)
    {
        var seconds = (int)Math.Ceiling(wait.TotalSeconds);
        Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return StatusCode(StatusCodes.Status429TooManyRequests, new { RetryAfterSeconds = seconds });
    }

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private sealed record ShareAccess(ShareLink Link, BaseItem Root, BaseItem Item);

    private sealed record SeasonView(string Id, string Name, int? Index, List<object> Episodes);

    private sealed record PluginConfigurationSnapshot(int RememberDays, int MaxFailuresPerLink, int MaxFailuresPerIp);
}
