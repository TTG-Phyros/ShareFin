#pragma warning disable SA1402, SA1649 // the séances and the state they hand out, kept together

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Sharefin.Party;

/// <summary>
/// What every viewer of a séance follows.
/// </summary>
/// <param name="Id">The séance id, put in the invitation link.</param>
/// <param name="ItemId">The video watched, or null until someone starts one.</param>
/// <param name="Paused">Whether the séance is paused.</param>
/// <param name="Position">Position in seconds, now.</param>
/// <param name="Version">Bumped at every change, so viewers wait for the next one.</param>
/// <param name="Viewers">Viewers seen in the last minute.</param>
public sealed record PartyState(string Id, Guid? ItemId, bool Paused, double Position, long Version, int Viewers);

/// <summary>
/// Séances: viewers of one link who watch the same video together, with play, pause, seek and
/// episode shared. Volume, audio, subtitles and quality stay each viewer's own (the page never sends them).
/// </summary>
/// <remarks>
/// shortcut: in memory, so séances end when Jellyfin restarts; persist them if that ever matters.
/// </remarks>
public sealed class WatchParties
{
    /// <summary>Séances open at once on one link, so a leaked link cannot fill memory.</summary>
    internal const int MaxPerLink = 20;

    private static readonly TimeSpan Idle = TimeSpan.FromHours(6);
    private static readonly TimeSpan Present = TimeSpan.FromMinutes(1); // longer than a long poll

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Party> _parties = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchParties"/> class.
    /// </summary>
    public WatchParties()
        : this(TimeProvider.System)
    {
    }

    internal WatchParties(TimeProvider time) => _time = time;

    /// <summary>
    /// Opens a séance on a link, paused and with no video yet.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="viewer">The viewer opening it.</param>
    /// <returns>The séance, or null when the link already has <see cref="MaxPerLink"/>.</returns>
    public PartyState? Create(Guid linkId, string viewer)
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            foreach (var stale in _parties.Values.Where(p => now - p.LastSeen > Idle).ToList())
            {
                _parties.Remove(stale.Id);
            }

            if (_parties.Values.Count(p => p.LinkId == linkId) >= MaxPerLink)
            {
                return null;
            }

            var party = new Party(linkId, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), now);
            _parties[party.Id] = party;
            Seen(party, viewer, now);
            return Snapshot(party, now);
        }
    }

    /// <summary>
    /// Changes what the séance plays. Left out values keep their current state; a new video without a position starts at 0.
    /// </summary>
    /// <param name="linkId">The link the viewer came through.</param>
    /// <param name="id">The séance.</param>
    /// <param name="viewer">The viewer making the change.</param>
    /// <param name="itemId">The new video, or null.</param>
    /// <param name="paused">Pause or play, or null.</param>
    /// <param name="position">The new position in seconds, or null.</param>
    /// <returns>The new state, or null when the séance does not exist on this link.</returns>
    public PartyState? Update(Guid linkId, string id, string viewer, Guid? itemId, bool? paused, double? position)
    {
        lock (_lock)
        {
            if (Find(linkId, id) is not { } party)
            {
                return null;
            }

            var now = _time.GetUtcNow();
            party.Position = position ?? (itemId is not null && itemId != party.ItemId ? 0 : PositionAt(party, now));
            party.Since = now;
            party.ItemId = itemId ?? party.ItemId;
            party.Paused = paused ?? party.Paused;
            Seen(party, viewer, now);
            Changed(party);
            return Snapshot(party, now);
        }
    }

    /// <summary>
    /// Waits for a change newer than <paramref name="since"/> (a long poll), or gives the current state after <paramref name="timeout"/>.
    /// </summary>
    /// <param name="linkId">The link the viewer came through.</param>
    /// <param name="id">The séance.</param>
    /// <param name="viewer">The viewer waiting, counted as present.</param>
    /// <param name="since">The last version the viewer has.</param>
    /// <param name="timeout">How long to wait at most.</param>
    /// <param name="cancellationToken">Cancelled when the browser goes away.</param>
    /// <returns>The state, or null when the séance does not exist on this link.</returns>
    public async Task<PartyState?> WaitAsync(Guid linkId, string id, string viewer, long since, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Task changed;
        lock (_lock)
        {
            if (Find(linkId, id) is not { } party)
            {
                return null;
            }

            var now = _time.GetUtcNow();
            if (Seen(party, viewer, now))
            {
                Changed(party); // someone joined: the others' viewer count updates
            }

            if (party.Version > since)
            {
                return Snapshot(party, now);
            }

            changed = party.Changed.Task;
        }

        await Task.WhenAny(changed, Task.Delay(timeout, _time, cancellationToken)).ConfigureAwait(false);
        lock (_lock)
        {
            return Find(linkId, id) is { } party ? Snapshot(party, _time.GetUtcNow()) : null;
        }
    }

    private static double PositionAt(Party party, DateTimeOffset now) =>
        party.Paused ? party.Position : party.Position + (now - party.Since).TotalSeconds;

    private static void Changed(Party party)
    {
        party.Version++;
        var waiting = party.Changed;
        party.Changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting.TrySetResult();
    }

    /// <returns>True when the viewer was not present.</returns>
    private static bool Seen(Party party, string viewer, DateTimeOffset now)
    {
        foreach (var gone in party.Viewers.Where(v => now - v.Value > Present).Select(v => v.Key).ToList())
        {
            party.Viewers.Remove(gone);
        }

        party.LastSeen = now;
        var joined = !party.Viewers.ContainsKey(viewer);
        party.Viewers[viewer] = now;
        return joined;
    }

    private static PartyState Snapshot(Party party, DateTimeOffset now) =>
        new(party.Id, party.ItemId, party.Paused, PositionAt(party, now), party.Version, party.Viewers.Count);

    private Party? Find(Guid linkId, string id) =>
        _parties.TryGetValue(id, out var party) && party.LinkId == linkId ? party : null;

    private sealed class Party(Guid linkId, string id, DateTimeOffset now)
    {
        public Guid LinkId { get; } = linkId;

        public string Id { get; } = id;

        public Guid? ItemId { get; set; }

        public bool Paused { get; set; } = true;

        public double Position { get; set; }

        public DateTimeOffset Since { get; set; } = now;

        public long Version { get; set; }

        public DateTimeOffset LastSeen { get; set; } = now;

        public TaskCompletionSource Changed { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Dictionary<string, DateTimeOffset> Viewers { get; } = new(StringComparer.Ordinal);
    }
}
