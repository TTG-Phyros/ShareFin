using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sharefin.Party;
using Xunit;

namespace Jellyfin.Plugin.Sharefin.Tests;

public class WatchPartiesTests
{
    private static readonly Guid Link = Guid.NewGuid();
    private static readonly Guid Film = Guid.NewGuid();
    private static readonly TimeSpan Long = TimeSpan.FromSeconds(30);

    private readonly Clock _clock = new();

    private WatchParties Parties() => new(_clock);

    [Fact]
    public void ThePositionRunsOnlyWhilePlaying()
    {
        var parties = Parties();
        var id = parties.Create(Link, "a")!.Id;
        parties.Update(Link, id, "a", Film, false, 100);
        _clock.Now += TimeSpan.FromSeconds(10);

        var paused = parties.Update(Link, id, "b", null, true, null)!;
        Assert.Equal(110, paused.Position, 3);
        Assert.Equal(Film, paused.ItemId);

        _clock.Now += TimeSpan.FromSeconds(10);
        Assert.Equal(110, parties.Update(Link, id, "b", null, false, null)!.Position, 3);
        Assert.Equal(0, parties.Update(Link, id, "a", Guid.NewGuid(), null, null)!.Position, 3); // a new video starts over
    }

    [Fact]
    public async Task ASeanceIsOnlyReachableThroughItsLink()
    {
        var parties = Parties();
        var id = parties.Create(Link, "a")!.Id;
        var other = Guid.NewGuid();
        Assert.Null(parties.Update(other, id, "a", null, false, null));
        Assert.Null(await parties.WaitAsync(other, id, "a", -1, Long, TestContext.Current.CancellationToken));
        Assert.Null(await parties.WaitAsync(Link, "unknown", "a", -1, Long, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWaitingViewerWakesUpOnAChange()
    {
        var parties = Parties();
        var created = parties.Create(Link, "a")!;
        var joined = await parties.WaitAsync(Link, created.Id, "b", -1, Long, TestContext.Current.CancellationToken);
        Assert.Equal(2, joined!.Viewers);
        Assert.True(joined.Version > created.Version); // "a" learns that "b" joined

        var waiting = parties.WaitAsync(Link, created.Id, "b", joined.Version, Long, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);
        parties.Update(Link, created.Id, "a", null, false, 42);
        var state = await waiting.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(state!.Paused);
        Assert.Equal(42, state.Position, 3);
    }

    [Fact]
    public async Task AWaitWithoutChangeEndsAtTheTimeout()
    {
        var parties = Parties();
        var created = parties.Create(Link, "a")!;
        var state = await parties.WaitAsync(Link, created.Id, "a", created.Version, TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        Assert.Equal(created.Version, state!.Version);
    }

    [Fact]
    public void ViewersGoneForAMinuteNoLongerCount()
    {
        var parties = Parties();
        var id = parties.Create(Link, "a")!.Id;
        parties.Update(Link, id, "b", null, null, null);
        _clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, parties.Update(Link, id, "a", null, null, null)!.Viewers);
    }

    [Fact]
    public void ALinkOpensALimitedNumberOfSeances()
    {
        var parties = Parties();
        for (var i = 0; i < WatchParties.MaxPerLink; i++)
        {
            Assert.NotNull(parties.Create(Link, "a"));
        }

        Assert.Null(parties.Create(Link, "a"));
        Assert.NotNull(parties.Create(Guid.NewGuid(), "a"));
        _clock.Now += TimeSpan.FromDays(1); // idle séances are dropped
        Assert.NotNull(parties.Create(Link, "a"));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 20, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
