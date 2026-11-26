using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sharefin.Content;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Party;
using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using Jellyfin.Plugin.Sharefin.Streaming;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Branding;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
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
    private static readonly Lazy<string> PageTemplate = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Sharefin.Web.share.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private readonly ShareLinkStore _store;
    private readonly ShareCookie _cookie;
    private readonly ShareGate _gate;
    private readonly SharePlayback _playback;
    private readonly ShareContent _content;
    private readonly WatchParties _parties;
    private readonly IImageProcessor _imageProcessor;
    private readonly ISubtitleEncoder _subtitleEncoder;
    private readonly IServerConfigurationManager _configurationManager;
    private readonly ILogger<ShareController> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareController"/> class.
    /// </summary>
    /// <param name="store">The link store.</param>
    /// <param name="cookie">The access cookie service.</param>
    /// <param name="gate">The access checks.</param>
    /// <param name="playback">The playbacks through ffmpeg.</param>
    /// <param name="content">What the page shows of the shared items.</param>
    /// <param name="parties">The séances.</param>
    /// <param name="imageProcessor">The image processor.</param>
    /// <param name="subtitleEncoder">The subtitle encoder.</param>
    /// <param name="configurationManager">The server configuration, for the custom CSS theme.</param>
    /// <param name="logger">The logger.</param>
    public ShareController(
        ShareLinkStore store,
        ShareCookie cookie,
        ShareGate gate,
        SharePlayback playback,
        ShareContent content,
        WatchParties parties,
        IImageProcessor imageProcessor,
        ISubtitleEncoder subtitleEncoder,
        IServerConfigurationManager configurationManager,
        ILogger<ShareController> logger)
    {
        _store = store;
        _cookie = cookie;
        _gate = gate;
        _playback = playback;
        _content = content;
        _parties = parties;
        _imageProcessor = imageProcessor;
        _subtitleEncoder = subtitleEncoder;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    private static int RememberDays => Plugin.Instance?.Configuration.RememberDays ?? 30;

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
        if (_gate.Find(token) is null)
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
        var login = _gate.Login(token, request.Password, ClientIp);
        if (login.IsGranted)
        {
            IssueCookie(login.Link);
            return NoContent();
        }

        return login.Status switch
        {
            ShareAccessStatus.Locked => TooMany(login.RetryAfter ?? TimeSpan.Zero),
            ShareAccessStatus.Refused => Unauthorized(),
            _ => NotFound(),
        };
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

        return Ok(ShareContent.Describe(access.Root));
    }

    /// <summary>
    /// Gets what the browser needs to choose between direct play and transcoding.
    /// No file path is returned. Without <see cref="ShareContent.AllowDownload"/>,
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

        var source = _content.LocalSource(access.Item);
        if (source is null)
        {
            return NotFound();
        }

        return Ok(await _content.PlaybackAsync(access.Item, source, _playback.IsBusy(viewer)).ConfigureAwait(false));
    }

    /// <summary>
    /// Streams the original file, with HTTP range support, for direct play and the download button.
    /// Not found unless <see cref="ShareContent.AllowDownload"/>.
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

        if (_content.DownloadFile(access.Item) is not { } file)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "private, no-store";
        // The attachment name only matters to the download button: a <video> ignores Content-Disposition.
        return PhysicalFile(file.Path, file.Mime, file.Name, enableRangeProcessing: true);
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

        var source = _content.LocalSource(access.Item);
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

        var source = _content.LocalSource(access.Item);
        if (source?.MediaStreams?.Any(s => s.Index == index && s.Type == MediaStreamType.Subtitle && s.IsTextSubtitleStream) != true)
        {
            return NotFound();
        }

        var startTicks = (long)(Math.Max(0, t) * TimeSpan.TicksPerSecond);
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

        var path = await _content.TrickplayTileAsync(access.Item, width, index).ConfigureAwait(false);
        if (path is null)
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

        var found = ShareContent.FindImage(access.Item, type);
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

    /// <summary>
    /// Opens a séance on the link: viewers who open its invitation watch together.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="sid">The viewer's random session id.</param>
    /// <returns>The séance, or 429 when the link already has too many.</returns>
    [HttpPost("party")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public ActionResult CreateParty([FromRoute] string token, [FromQuery] string? sid = null)
    {
        NoStore();
        if (!TryAuthorize(token, null, out var access, out var error))
        {
            return error;
        }

        if (Viewer.TryCreate(access.Link, ClientIp, sid) is not { } viewer)
        {
            return BadRequest();
        }

        var state = _parties.Create(access.Link.Id, viewer.Id);
        return state is null ? StatusCode(StatusCodes.Status429TooManyRequests) : Ok(state);
    }

    /// <summary>
    /// Gets a séance once it changes after version <paramref name="v"/>, or after 25 s (a long poll, which goes through any proxy).
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="id">The séance.</param>
    /// <param name="v">The last version the viewer has, -1 for none.</param>
    /// <param name="sid">The viewer's random session id.</param>
    /// <returns>The state, or 404 when the séance is gone.</returns>
    [HttpGet("party/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> GetParty([FromRoute] string token, [FromRoute] string id, [FromQuery] long v = -1, [FromQuery] string? sid = null)
    {
        NoStore();
        if (!TryAuthorize(token, null, out var access, out var error))
        {
            return error;
        }

        if (Viewer.TryCreate(access.Link, ClientIp, sid) is not { } viewer)
        {
            return BadRequest();
        }

        var state = await _parties.WaitAsync(access.Link.Id, id, viewer.Id, v, TimeSpan.FromSeconds(25), HttpContext.RequestAborted).ConfigureAwait(false);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>
    /// Plays, pauses, seeks or changes the video for everyone in a séance.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="id">The séance.</param>
    /// <param name="request">What changes.</param>
    /// <param name="sid">The viewer's random session id.</param>
    /// <returns>The new state, or 404 when the séance is gone or the video is outside the link.</returns>
    [HttpPost("party/{id}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult UpdateParty([FromRoute] string token, [FromRoute] string id, [FromBody] PartyRequest request, [FromQuery] string? sid = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        NoStore();
        if (request.Position is { } p && !(double.IsFinite(p) && p is >= 0 and < 1e6))
        {
            return BadRequest();
        }

        // With an item, the gate also checks it belongs to the link.
        if (!TryAuthorize(token, request.ItemId, out var access, out var error))
        {
            return error;
        }

        if (Viewer.TryCreate(access.Link, ClientIp, sid) is not { } viewer)
        {
            return BadRequest();
        }

        var state = _parties.Update(access.Link.Id, id, viewer.Id, request.ItemId, request.Paused, request.Position);
        return state is null ? NotFound() : Ok(state);
    }

    /// <summary>
    /// Asks the gate, with the link's access cookie, and turns a refusal into the HTTP answer.
    /// </summary>
    private bool TryAuthorize(string token, Guid? itemId, [NotNullWhen(true)] out ShareAccess? access, out ActionResult error)
    {
        var status = _gate.Authorize(token, Request.Cookies[ShareCookie.CookieName(token)], itemId, out access);
        error = status == ShareAccessStatus.Refused ? Unauthorized() : NotFound();
        return access is not null;
    }

    private void IssueCookie(ShareLink link)
    {
        var now = DateTimeOffset.UtcNow;
        var expires = ShareCookie.ExpiryFor(link, RememberDays, now);
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
}
