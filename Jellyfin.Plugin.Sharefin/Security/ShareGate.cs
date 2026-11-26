#pragma warning disable SA1402, SA1649 // the gate and the answers it gives, kept together

using System;
using System.Diagnostics.CodeAnalysis;
using Jellyfin.Plugin.Sharefin.Configuration;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sharefin.Security;

/// <summary>
/// What the gate answers to a visitor.
/// </summary>
public enum ShareAccessStatus
{
    /// <summary>Access granted.</summary>
    Granted,

    /// <summary>Wrong password, or no valid access cookie.</summary>
    Refused,

    /// <summary>Too many wrong passwords: wait before trying again.</summary>
    Locked,

    /// <summary>Unknown, expired or revoked link, shared item gone, or item outside the link.</summary>
    NotFound,
}

/// <summary>
/// The answer to a password attempt.
/// </summary>
/// <param name="Status">The answer.</param>
/// <param name="Link">The link, when granted.</param>
/// <param name="RetryAfter">How long to wait, when locked.</param>
public sealed record ShareLogin(ShareAccessStatus Status, ShareLink? Link = null, TimeSpan? RetryAfter = null)
{
    /// <summary>Gets a value indicating whether the password was accepted.</summary>
    [MemberNotNullWhen(true, nameof(Link))]
    public bool IsGranted => Status == ShareAccessStatus.Granted && Link is not null;
}

/// <summary>
/// What a granted content or media request may reach.
/// </summary>
/// <param name="Link">The link.</param>
/// <param name="Root">The shared item.</param>
/// <param name="Item">The requested item: the shared item or one of its descendants.</param>
public sealed record ShareAccess(ShareLink Link, BaseItem Root, BaseItem Item);

/// <summary>
/// The one place that decides whether a visitor gets through a share link: the link is usable,
/// the password is right (with attempts throttled), the access cookie is current, and the
/// requested item belongs to the shared one.
/// </summary>
public sealed class ShareGate
{
    private readonly ShareLinkStore _store;
    private readonly ShareCookie _cookie;
    private readonly LoginThrottle _throttle;
    private readonly Func<Guid, BaseItem?> _findItem;
    private readonly Func<PluginConfiguration> _config;
    private readonly ILogger<ShareGate> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareGate"/> class.
    /// </summary>
    /// <param name="store">The link store.</param>
    /// <param name="cookie">The access cookie service.</param>
    /// <param name="throttle">The password attempt limiter.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="logger">The logger.</param>
    public ShareGate(ShareLinkStore store, ShareCookie cookie, LoginThrottle throttle, ILibraryManager libraryManager, ILogger<ShareGate> logger)
        : this(store, cookie, throttle, id => libraryManager.GetItemById(id), () => Plugin.Instance?.Configuration ?? new PluginConfiguration(), logger)
    {
    }

    internal ShareGate(ShareLinkStore store, ShareCookie cookie, LoginThrottle throttle, Func<Guid, BaseItem?> findItem, Func<PluginConfiguration> config, ILogger<ShareGate> logger)
    {
        _store = store;
        _cookie = cookie;
        _throttle = throttle;
        _findItem = findItem;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Finds a usable link: known, not expired, not revoked.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <returns>The link, or null.</returns>
    public ShareLink? Find(string token)
    {
        var link = _store.GetByToken(token);
        return link is not null && link.IsActive(DateTime.UtcNow) ? link : null;
    }

    /// <summary>
    /// Checks a password attempt. A locked link or address is refused before the password is even checked.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="password">The password typed.</param>
    /// <param name="ip">The client address.</param>
    /// <returns>Granted (issue the cookie), refused, locked or not found.</returns>
    public ShareLogin Login(string token, string? password, string ip)
    {
        var link = Find(token);
        if (link is null)
        {
            return new ShareLogin(ShareAccessStatus.NotFound);
        }

        if (_throttle.RetryAfter(link.Token, ip) is { } wait)
        {
            return new ShareLogin(ShareAccessStatus.Locked, RetryAfter: wait);
        }

        if (!SharePassword.Verify(password, link.PasswordHash))
        {
            var config = _config();
            _throttle.Fail(link.Token, ip, config.MaxFailuresPerLink, config.MaxFailuresPerIp);
            _logger.LogInformation("Sharefin: wrong password for link {LinkId} from {Ip}", link.Id, ip);
            return _throttle.RetryAfter(link.Token, ip) is { } lockWait
                ? new ShareLogin(ShareAccessStatus.Locked, RetryAfter: lockWait)
                : new ShareLogin(ShareAccessStatus.Refused);
        }

        _throttle.Succeed(link.Token);
        return new ShareLogin(ShareAccessStatus.Granted, link);
    }

    /// <summary>
    /// Checks a content or media request: the check every such endpoint goes through.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="cookie">The value of the link's access cookie, see <see cref="ShareCookie.CookieName"/>.</param>
    /// <param name="itemId">The requested item, or null for the shared item itself.</param>
    /// <param name="access">What the visitor may reach, set only when granted.</param>
    /// <returns>Granted, refused (no valid cookie) or not found; never locked.</returns>
    public ShareAccessStatus Authorize(string token, string? cookie, Guid? itemId, out ShareAccess? access)
    {
        access = null;
        var link = Find(token);
        if (link is null)
        {
            return ShareAccessStatus.NotFound;
        }

        if (!_cookie.Validate(cookie, link))
        {
            return ShareAccessStatus.Refused;
        }

        var root = _findItem(link.ItemId);
        if (root is null)
        {
            return ShareAccessStatus.NotFound;
        }

        var item = itemId is null || itemId.Value.Equals(root.Id) ? root : _findItem(itemId.Value);
        if (item is null || !ShareScope.Contains(root, item))
        {
            return ShareAccessStatus.NotFound;
        }

        access = new ShareAccess(link, root, item);
        return ShareAccessStatus.Granted;
    }
}
