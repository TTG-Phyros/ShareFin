using System;
using System.IO;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using Jellyfin.Plugin.Sharefin.Web;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.AspNetCore.DataProtection;
using Xunit;

namespace Jellyfin.Plugin.Sharefin.Tests;

public class PasswordTests
{
    [Fact]
    public void RightPasswordVerifies()
    {
        var hash = SharePassword.Hash("correct horse");
        Assert.True(SharePassword.Verify("correct horse", hash));
    }

    [Theory]
    [InlineData("wrong")]
    [InlineData("correct hors")]
    [InlineData("")]
    [InlineData(null)]
    public void WrongPasswordFails(string? attempt)
    {
        Assert.False(SharePassword.Verify(attempt, SharePassword.Hash("correct horse")));
    }

    [Fact]
    public void HashIsSaltedAndNeverTheClearPassword()
    {
        var a = SharePassword.Hash("secret123");
        var b = SharePassword.Hash("secret123");
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("secret123", a, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("1.!!!.!!!")]
    [InlineData("0.AAAA.AAAA")]
    public void MalformedHashFails(string stored)
    {
        Assert.False(SharePassword.Verify("x", stored));
    }

    [Fact]
    public void TokensAreLongAndUnique()
    {
        var a = SharePassword.NewToken();
        Assert.True(a.Length >= 43); // 256 bits base64url
        Assert.NotEqual(a, SharePassword.NewToken());
    }
}

public class LinkStateTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PermanentLinkIsActive() => Assert.True(new ShareLink().IsActive(Now));

    [Fact]
    public void FutureExpiryIsActive() => Assert.True(new ShareLink { ExpiresUtc = Now.AddMinutes(1) }.IsActive(Now));

    [Fact]
    public void PastExpiryIsInactive() => Assert.False(new ShareLink { ExpiresUtc = Now.AddSeconds(-1) }.IsActive(Now));

    [Fact]
    public void ExpiryAtNowIsInactive() => Assert.False(new ShareLink { ExpiresUtc = Now }.IsActive(Now));

    [Fact]
    public void RevokedLinkIsInactive() => Assert.False(new ShareLink { RevokedUtc = Now.AddDays(-1) }.IsActive(Now));
}

public class CookieTests
{
    private readonly ShareCookie _cookie = new(new EphemeralDataProtectionProvider());

    private static ShareLink NewLink() => new()
    {
        Id = Guid.NewGuid(),
        Token = SharePassword.NewToken(),
        Stamp = SharePassword.NewToken(16),
    };

    private string IssueFor(ShareLink link) => _cookie.Issue(link, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow);

    [Fact]
    public void FreshCookieIsValid()
    {
        var link = NewLink();
        Assert.True(_cookie.Validate(IssueFor(link), link));
    }

    [Fact]
    public void PasswordChangeOrRevocationInvalidatesCookie()
    {
        var link = NewLink();
        var value = IssueFor(link);
        link.Stamp = SharePassword.NewToken(16); // what AdminController does on password change and revoke
        Assert.False(_cookie.Validate(value, link));
    }

    [Fact]
    public void CookieOfAnotherLinkIsRejected()
    {
        var a = NewLink();
        var b = NewLink();
        b.Stamp = a.Stamp;
        Assert.False(_cookie.Validate(IssueFor(a), b));
    }

    [Fact]
    public void ExpiredCookieIsRejected()
    {
        var link = NewLink();
        var value = _cookie.Issue(link, DateTimeOffset.UtcNow.AddSeconds(-1), DateTimeOffset.UtcNow);
        Assert.False(_cookie.Validate(value, link));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-cookie")]
    public void GarbageIsRejected(string? value) => Assert.False(_cookie.Validate(value, NewLink()));

    [Fact]
    public void TamperedCookieIsRejected()
    {
        var link = NewLink();
        var value = IssueFor(link).ToCharArray();
        value[value.Length / 2] = value[value.Length / 2] == 'A' ? 'B' : 'A';
        Assert.False(_cookie.Validate(new string(value), link));
    }

    [Fact]
    public void CookieFromAnotherKeyIsRejected()
    {
        var link = NewLink();
        var other = new ShareCookie(new EphemeralDataProtectionProvider());
        Assert.False(_cookie.Validate(other.Issue(link, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow), link));
    }

    [Fact]
    public void CookieNameDependsOnLink() => Assert.NotEqual(ShareCookie.CookieName("a"), ShareCookie.CookieName("b"));

    [Fact]
    public void TemporaryLinkCookieLastsUntilLinkExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var expires = now.UtcDateTime.AddDays(3);
        Assert.Equal(expires, ShareCookie.ExpiryFor(new ShareLink { ExpiresUtc = expires }, 30, now).UtcDateTime);
    }

    [Fact]
    public void PermanentLinkCookieSlides() => Assert.Equal(
        TimeSpan.FromDays(30),
        ShareCookie.ExpiryFor(new ShareLink(), 30, DateTimeOffset.UnixEpoch) - DateTimeOffset.UnixEpoch);
}

public class ScopeTests
{
    private readonly Series _series = new() { Id = Guid.NewGuid() };
    private readonly Series _otherSeries = new() { Id = Guid.NewGuid() };

    private Season SeasonOf(Series series) => new() { Id = Guid.NewGuid(), SeriesId = series.Id };

    private static Episode EpisodeOf(Season season) => new() { Id = Guid.NewGuid(), SeriesId = season.SeriesId, SeasonId = season.Id };

    [Fact]
    public void SeriesGivesItsSeasonsAndEpisodes()
    {
        var season = SeasonOf(_series);
        Assert.True(ShareScope.Contains(_series, _series));
        Assert.True(ShareScope.Contains(_series, season));
        Assert.True(ShareScope.Contains(_series, EpisodeOf(season)));
    }

    [Fact]
    public void SeriesDoesNotGiveAnotherSeries()
    {
        var season = SeasonOf(_otherSeries);
        Assert.False(ShareScope.Contains(_series, _otherSeries));
        Assert.False(ShareScope.Contains(_series, season));
        Assert.False(ShareScope.Contains(_series, EpisodeOf(season)));
    }

    [Fact]
    public void SeasonGivesOnlyItsOwnEpisodes()
    {
        var season = SeasonOf(_series);
        var sibling = SeasonOf(_series);
        Assert.True(ShareScope.Contains(season, EpisodeOf(season)));
        Assert.False(ShareScope.Contains(season, EpisodeOf(sibling)));
        Assert.False(ShareScope.Contains(season, sibling));
        Assert.False(ShareScope.Contains(season, _series)); // never the parent
    }

    [Fact]
    public void EpisodeGivesOnlyItself()
    {
        var season = SeasonOf(_series);
        var episode = EpisodeOf(season);
        Assert.True(ShareScope.Contains(episode, episode));
        Assert.False(ShareScope.Contains(episode, EpisodeOf(season)));
        Assert.False(ShareScope.Contains(episode, season));
        Assert.False(ShareScope.Contains(episode, _series));
    }

    [Fact]
    public void MovieGivesOnlyItself()
    {
        var movie = new Movie { Id = Guid.NewGuid() };
        Assert.True(ShareScope.Contains(movie, movie));
        Assert.False(ShareScope.Contains(movie, new Movie { Id = Guid.NewGuid() }));
        Assert.False(ShareScope.Contains(movie, _series));
    }
}

public class ThrottleTests
{
    private readonly ManualClock _clock = new();
    private readonly LoginThrottle _throttle;

    public ThrottleTests() => _throttle = new LoginThrottle(_clock);

    private void Fail(string token, string ip, int times)
    {
        for (var i = 0; i < times; i++)
        {
            _throttle.Fail(token, ip, 5, 20);
        }
    }

    [Fact]
    public void LocksLinkAfterMaxFailures()
    {
        Fail("t", "1.1.1.1", 4);
        Assert.Null(_throttle.RetryAfter("t", "1.1.1.1"));
        Fail("t", "1.1.1.1", 1);
        Assert.Equal(TimeSpan.FromMinutes(1), _throttle.RetryAfter("t", "1.1.1.1"));
        Assert.NotNull(_throttle.RetryAfter("t", "2.2.2.2")); // lock is per link, from any address
    }

    [Fact]
    public void LockExpiresThenDoubles()
    {
        Fail("t", "ip", 5);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(_throttle.RetryAfter("t", "ip"));
        Fail("t", "ip", 5);
        Assert.Equal(TimeSpan.FromMinutes(2), _throttle.RetryAfter("t", "ip"));
    }

    [Fact]
    public void LockIsCappedAt15Minutes()
    {
        for (var i = 0; i < 10; i++)
        {
            Fail("t", "ip" + i, 5);
            _clock.Advance(TimeSpan.FromMinutes(16));
        }

        Fail("t", "last", 5);
        Assert.Equal(TimeSpan.FromMinutes(15), _throttle.RetryAfter("t", "last"));
    }

    [Fact]
    public void LocksIpAcrossLinks()
    {
        for (var i = 0; i < 20; i++)
        {
            _throttle.Fail("token" + i, "6.6.6.6", 5, 20);
        }

        Assert.NotNull(_throttle.RetryAfter("fresh-token", "6.6.6.6"));
        Assert.Null(_throttle.RetryAfter("fresh-token", "7.7.7.7"));
    }

    [Fact]
    public void FailuresOutsideTheWindowAreForgotten()
    {
        Fail("t", "ip", 4);
        _clock.Advance(TimeSpan.FromMinutes(16));
        Fail("t", "ip", 1);
        Assert.Null(_throttle.RetryAfter("t", "ip"));
    }

    [Fact]
    public void SuccessResetsLinkCounter()
    {
        Fail("t", "ip", 4);
        _throttle.Succeed("t");
        Fail("t", "ip", 4);
        Assert.Null(_throttle.RetryAfter("t", "ip"));
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

public sealed class StoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "sharefin-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void LinksSurviveReload()
    {
        var store = new ShareLinkStore(_dir);
        var link = new ShareLink { Id = Guid.NewGuid(), Token = "abc", PasswordHash = SharePassword.Hash("secret123"), Stamp = "s1" };
        store.Add(link);
        Assert.True(store.Update(link.Id, l => l.Stamp = "s2"));

        var reloaded = new ShareLinkStore(_dir).GetByToken("abc");
        Assert.NotNull(reloaded);
        Assert.Equal("s2", reloaded.Stamp);
        Assert.True(SharePassword.Verify("secret123", reloaded.PasswordHash));
    }

    [Fact]
    public void RemoveDeletes()
    {
        var store = new ShareLinkStore(_dir);
        var link = new ShareLink { Id = Guid.NewGuid(), Token = "abc" };
        store.Add(link);
        Assert.True(store.Remove(link.Id));
        Assert.Null(new ShareLinkStore(_dir).GetByToken("abc"));
        Assert.False(store.Remove(link.Id));
    }
}

public class IndexPatchTests
{
    [Fact]
    public void InsertsScriptOnceBeforeBody()
    {
        var once = IndexPatch.Patch(new PatchPayload { Contents = "<html><body><div></div></body></html>" });
        Assert.Contains("Créer un lien de partage", once, StringComparison.Ordinal);
        Assert.EndsWith("</script></body></html>", once, StringComparison.Ordinal);
        Assert.Equal(once, IndexPatch.Patch(new PatchPayload { Contents = once }));
    }
}
