using System;
using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// Counts the viewers streaming through ffmpeg on each link, so a leaked link cannot start
/// an unbounded number of jobs. A viewer that already streams (a seek, which overlaps the
/// request it replaces) never needs a second slot.
/// </summary>
/// <remarks>
/// shortcut: in memory, so counts reset when Jellyfin restarts, along with the jobs themselves.
/// </remarks>
internal sealed class StreamLimiter
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Dictionary<string, int>> _active = [];

    /// <summary>
    /// Takes a slot for a viewer, unless the link already has <paramref name="max"/> other viewers.
    /// Every successful call must be matched by one <see cref="Leave"/>.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="viewer">The viewer's device id.</param>
    /// <param name="max">Viewers allowed per link.</param>
    /// <returns>False when the link is full.</returns>
    public bool TryEnter(Guid linkId, string viewer, int max)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(linkId, out var viewers))
            {
                viewers = new Dictionary<string, int>(StringComparer.Ordinal);
                _active[linkId] = viewers;
            }

            viewers.TryGetValue(viewer, out var requests);
            if (requests == 0 && viewers.Count >= Math.Max(1, max))
            {
                return false;
            }

            viewers[viewer] = requests + 1;
            return true;
        }
    }

    /// <summary>
    /// Gets whether a new viewer would be refused.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="viewer">The viewer's device id.</param>
    /// <param name="max">Viewers allowed per link.</param>
    /// <returns>True when <see cref="TryEnter"/> would fail.</returns>
    public bool IsFull(Guid linkId, string viewer, int max)
    {
        lock (_gate)
        {
            return _active.TryGetValue(linkId, out var viewers) && !viewers.ContainsKey(viewer) && viewers.Count >= Math.Max(1, max);
        }
    }

    /// <summary>
    /// Releases a slot taken by <see cref="TryEnter"/>.
    /// </summary>
    /// <param name="linkId">The link.</param>
    /// <param name="viewer">The viewer's device id.</param>
    public void Leave(Guid linkId, string viewer)
    {
        lock (_gate)
        {
            if (!_active.TryGetValue(linkId, out var viewers) || !viewers.TryGetValue(viewer, out var requests))
            {
                return;
            }

            if (requests > 1)
            {
                viewers[viewer] = requests - 1;
                return;
            }

            viewers.Remove(viewer);
            if (viewers.Count == 0)
            {
                _active.Remove(linkId);
            }
        }
    }
}
