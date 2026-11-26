using System;
using System.Collections.Generic;
using System.IO;
using Jellyfin.Plugin.Sharefin.Configuration;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Sharefin.Tests;

/// <summary>
/// Whole visits through a share link: password, throttling, cookie and scope together.
/// </summary>
public sealed class ShareGateTests : IDisposable
{
    private const string Password = "correct horse";
    private const string Ip = "10.0.0.1";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sharefin-tests-" + Guid.NewGuid().ToString("N"));
    private readonly ManualClock _clock = new();
    private readonly ShareCookie _cookie = new(new EphemeralDataProtectionProvider());
    private readonly Dictionary<Guid, BaseItem> _library = [];
    private readonly ShareLinkStore _store;
    private readonly ShareGate _gate;
    private readonly Series _series = new() { Id = Guid.NewGuid() };
    private readonly Season _season;
    private readonly Episode _episode;
    private readonly Episode _otherEpisode;

    public ShareGateTests()
    {
        _store = new ShareLinkStore(_dir);
        _gate = new ShareGate(
            _store,
            _cookie,
            new LoginThrottle(_clock),
            id => _library.GetValueOrDefault(id),
            () => new PluginConfiguration { MaxFailuresPerLink = 5, MaxFailuresPerIp = 20 },
            NullLogger<ShareGate>.Instance);

        _season = new Season { Id = Guid.NewGuid(), SeriesId = _series.Id };
        _episode = new Episode { Id = Guid.NewGuid(), SeriesId = _series.Id, SeasonId = _season.Id };
        var otherSeries = new Series { Id = Guid.NewGuid() };
        _otherEpisode = new Episode { Id = Guid.NewGuid(), SeriesId = otherSeries.Id, SeasonId = Guid.NewGuid() };
        foreach (var item in new BaseItem[] { _series, _season, _episode, otherSeries, _otherEpisode })
        {
            _library[item.Id] = item;
        }
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private ShareLink Share(BaseItem item, DateTime? expires = null)
    {
        var link = new ShareLink { Id = Guid.NewGuid(), Token = SharePassword.NewToken(), ItemId = item.Id, ExpiresUtc = expires, PasswordHash = SharePassword.Hash(Password), Stamp = SharePassword.NewToken(16) };
        _store.Add(link);
        return link;
    }

    /// <summary>What the browser keeps after a right password.</summary>
    private string LogIn(ShareLink link)
    {
        var login = _gate.Login(link.Token, Password, Ip);
        Assert.True(login.IsGranted);
        return _cookie.Issue(login.Link, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RightPasswordThenEpisodeOfTheSharedSeries()
    {
        var link = Share(_series);
        var cookie = LogIn(link);

        Assert.Equal(ShareAccessStatus.Granted, _gate.Authorize(link.Token, cookie, null, out var root));
        Assert.Same(_series, root!.Item);
        Assert.Equal(ShareAccessStatus.Granted, _gate.Authorize(link.Token, cookie, _episode.Id, out var episode));
        Assert.Same(_episode, episode!.Item);
        Assert.Same(_series, episode.Root);
    }

    [Fact]
    public void FiveWrongPasswordsLockEvenTheRightOneUntilTheLockEnds()
    {
        var link = Share(_series);
        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(ShareAccessStatus.Refused, _gate.Login(link.Token, "wrong", Ip).Status);
        }

        var fifth = _gate.Login(link.Token, "wrong", Ip);
        Assert.Equal(ShareAccessStatus.Locked, fifth.Status);
        Assert.Equal(TimeSpan.FromMinutes(1), fifth.RetryAfter);

        Assert.Equal(ShareAccessStatus.Locked, _gate.Login(link.Token, Password, Ip).Status);
        Assert.Equal(ShareAccessStatus.Locked, _gate.Login(link.Token, Password, "10.0.0.2").Status); // the link is locked, from any address

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(_gate.Login(link.Token, Password, Ip).IsGranted);
    }

    [Fact]
    public void RightPasswordClearsTheLinkCounter()
    {
        var link = Share(_series);
        for (var i = 0; i < 4; i++)
        {
            _gate.Login(link.Token, "wrong", Ip);
        }

        Assert.True(_gate.Login(link.Token, Password, Ip).IsGranted);
        Assert.Equal(ShareAccessStatus.Refused, _gate.Login(link.Token, "wrong", Ip).Status); // a fresh count, no lock yet
    }

    [Fact]
    public void UnknownExpiredOrRevokedLinkIsNotFound()
    {
        var expired = Share(_series, DateTime.UtcNow.AddSeconds(-1));
        var revoked = Share(_series);
        var cookie = LogIn(revoked);
        _store.Update(revoked.Id, l => l.RevokedUtc = DateTime.UtcNow);

        foreach (var token in new[] { "nope", expired.Token, revoked.Token })
        {
            Assert.Equal(ShareAccessStatus.NotFound, _gate.Login(token, Password, Ip).Status);
            Assert.Equal(ShareAccessStatus.NotFound, _gate.Authorize(token, cookie, null, out var access));
            Assert.Null(access);
        }
    }

    [Fact]
    public void NoCookieOrAnotherLinksCookieIsRefused()
    {
        var link = Share(_series);
        var other = Share(_series);
        Assert.Equal(ShareAccessStatus.Refused, _gate.Authorize(link.Token, null, null, out _));
        Assert.Equal(ShareAccessStatus.Refused, _gate.Authorize(link.Token, LogIn(other), null, out _));
    }

    [Fact]
    public void PasswordChangeLogsVisitorsOut()
    {
        var link = Share(_series);
        var cookie = LogIn(link);
        _store.Update(link.Id, l =>
        {
            l.PasswordHash = SharePassword.Hash("new password");
            l.Stamp = SharePassword.NewToken(16); // what AdminController does
        });

        Assert.Equal(ShareAccessStatus.Refused, _gate.Authorize(link.Token, cookie, null, out _));
        Assert.Equal(ShareAccessStatus.Refused, _gate.Login(link.Token, Password, Ip).Status);
        Assert.True(_gate.Login(link.Token, "new password", Ip).IsGranted);
    }

    [Fact]
    public void EpisodeLinkDoesNotOpenItsSeriesOrSiblings()
    {
        var link = Share(_episode);
        var cookie = LogIn(link);
        var sibling = new Episode { Id = Guid.NewGuid(), SeriesId = _series.Id, SeasonId = _season.Id };
        _library[sibling.Id] = sibling;

        Assert.Equal(ShareAccessStatus.Granted, _gate.Authorize(link.Token, cookie, _episode.Id, out _));
        Assert.Equal(ShareAccessStatus.NotFound, _gate.Authorize(link.Token, cookie, sibling.Id, out _));
        Assert.Equal(ShareAccessStatus.NotFound, _gate.Authorize(link.Token, cookie, _series.Id, out _));
    }

    [Fact]
    public void ItemOutsideTheShareOrMissingIsNotFound()
    {
        var link = Share(_series);
        var cookie = LogIn(link);
        Assert.Equal(ShareAccessStatus.NotFound, _gate.Authorize(link.Token, cookie, _otherEpisode.Id, out _));
        Assert.Equal(ShareAccessStatus.NotFound, _gate.Authorize(link.Token, cookie, Guid.NewGuid(), out _));

        _library.Remove(_series.Id); // the shared item left the library
        Assert.Equal(ShareAccessStatus.NotFound, _gate.Authorize(link.Token, cookie, null, out _));
    }
}
