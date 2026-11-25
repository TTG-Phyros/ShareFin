using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Jellyfin.Plugin.Sharefin.Configuration;
using Jellyfin.Plugin.Sharefin.Model;
using Jellyfin.Plugin.Sharefin.Streaming;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Sharefin.Tests;

public class SharePlaybackTests
{
    private static readonly ShareLink Link = new() { Id = Guid.NewGuid() };
    private static readonly Movie Video = new() { Id = Guid.NewGuid() };
    private static readonly MediaSourceInfo Source = new() { Id = "src", MediaStreams = [new MediaStream { Type = MediaStreamType.Audio, Index = 1 }] };
    private static readonly PlaybackRequest Best = new(0, null, true, true, null, null);

    private readonly FakeJobs _jobs = new();

    private SharePlayback Playback(int maxStreams) => new(_jobs, () => new PluginConfiguration { MaxStreamsPerLink = maxStreams });

    private static Viewer Tab(string sid, ShareLink? link = null) => Viewer.TryCreate(link ?? Link, "10.0.0.1", sid)!;

    private static async Task<ActivePlayback> Started(SharePlayback playback, Viewer viewer, PlaybackRequest? request = null)
    {
        var start = await playback.StartAsync(viewer, Video, Source, request ?? Best);
        Assert.Equal(PlaybackStatus.Started, start.Status);
        return start.Playback!;
    }

    [Fact]
    public async Task RefusesViewersBeyondTheLimit()
    {
        var playback = Playback(2);
        await Started(playback, Tab("a"));
        await Started(playback, Tab("b"));
        Assert.True(playback.IsBusy(Tab("c")));
        Assert.Equal(PlaybackStatus.Busy, (await playback.StartAsync(Tab("c"), Video, Source, Best)).Status);
        await Started(playback, Tab("c", new ShareLink { Id = Guid.NewGuid() })); // other links are counted apart
    }

    [Fact]
    public async Task ASeekStopsThePreviousJobWithoutTakingASecondSlot()
    {
        var playback = Playback(1);
        var first = await Started(playback, Tab("a"));
        Assert.False(playback.IsBusy(Tab("a")));
        var seek = await Started(playback, Tab("a")); // the new request starts before the old one ends
        Assert.Equal(2, _jobs.ViewerStops.Count);
        Assert.Equal(_jobs.ViewerStops[0], _jobs.ViewerStops[1]);

        await first.DisposeAsync();
        Assert.True(playback.IsBusy(Tab("b"))); // "a" still streams
        await seek.DisposeAsync();
        Assert.False(playback.IsBusy(Tab("b")));
    }

    [Fact]
    public async Task ClosingThePlaybackFreesTheSlotAndStopsTheJobOnce()
    {
        var playback = Playback(1);
        var a = await Started(playback, Tab("a"));
        await a.DisposeAsync();
        await a.DisposeAsync();
        Assert.Equal([_jobs.Started[0].PlaySessionId], _jobs.SessionStops);
        await Started(playback, Tab("b"));
    }

    [Fact]
    public async Task AFailedStartFreesTheSlot()
    {
        var playback = Playback(1);
        _jobs.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => playback.StartAsync(Tab("a"), Video, Source, Best));
        Assert.Single(_jobs.SessionStops);

        _jobs.Fail = false;
        await Started(playback, Tab("b"));
    }

    [Theory]
    [InlineData(999, null)] // no such quality
    [InlineData(null, 7)] // no such audio track
    public async Task InvalidChoicesStartNothing(int? height, int? audio)
    {
        var playback = Playback(1);
        var start = await playback.StartAsync(Tab("a"), Video, Source, new PlaybackRequest(0, audio, false, false, height, null));
        Assert.Equal(PlaybackStatus.Invalid, start.Status);
        Assert.Empty(_jobs.Started);
        Assert.False(playback.IsBusy(Tab("b")));
    }

    [Fact]
    public async Task ALowerQualityAlwaysEncodesTheVideo()
    {
        await Started(Playback(2), Tab("a"), new PlaybackRequest(90, 1, true, true, 720, null));
        var job = _jobs.Started[0];
        Assert.False(job.CopyVideo);
        Assert.True(job.CopyAudio);
        Assert.Equal(720, job.MaxHeight);
        Assert.Equal(90 * TimeSpan.TicksPerSecond, job.StartTicks);
    }

    [Fact]
    public async Task TheBestQualityCopiesWhatTheBrowserPlays()
    {
        await Started(Playback(1), Tab("a"));
        Assert.True(_jobs.Started[0].CopyVideo);
        Assert.Equal(1080, _jobs.Started[0].MaxHeight);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a-b")]
    [InlineData("x123456789012345678901234567890123456789012345678901234567890123456")]
    public void MalformedSessionIdsAreRefused(string sid) => Assert.Null(Viewer.TryCreate(Link, "10.0.0.1", sid));

    [Fact]
    public async Task TabsBehindOneAddressAreDistinctViewers()
    {
        var playback = Playback(1);
        await Started(playback, Tab("a"));
        Assert.True(playback.IsBusy(Tab("b")));
        Assert.False(playback.IsBusy(Tab("a")));
    }

    private sealed class FakeJobs : IPlaybackJobs
    {
        public bool Fail { get; set; }

        public List<PlaybackJob> Started { get; } = [];

        public List<string> ViewerStops { get; } = [];

        public List<string> SessionStops { get; } = [];

        public Task<Stream> StartAsync(PlaybackJob job)
        {
            if (Fail)
            {
                throw new IOException("ffmpeg did not start");
            }

            Started.Add(job);
            return Task.FromResult<Stream>(new MemoryStream());
        }

        public Task StopViewerAsync(string viewerId)
        {
            ViewerStops.Add(viewerId);
            return Task.CompletedTask;
        }

        public Task StopSessionAsync(string playSessionId)
        {
            SessionStops.Add(playSessionId);
            return Task.CompletedTask;
        }
    }
}
