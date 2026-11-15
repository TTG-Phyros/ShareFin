using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.Sharefin.Security;

/// <summary>
/// Limits wrong password attempts per link and per IP address. After too many failures
/// the key is locked for 1 minute, doubling on each new lock up to 15 minutes.
/// </summary>
/// <remarks>
/// shortcut: in memory, so counters reset when Jellyfin restarts; persist them if that becomes an attack path.
/// </remarks>
public sealed class LoginThrottle
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FirstLock = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxLock = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan Forget = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginThrottle"/> class.
    /// </summary>
    public LoginThrottle()
        : this(TimeProvider.System)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="LoginThrottle"/> class with a given clock.
    /// </summary>
    /// <param name="time">The clock.</param>
    internal LoginThrottle(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Gets how long the caller must wait before trying again, or null when allowed.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="ip">The client IP address.</param>
    /// <returns>The remaining lock time, or null.</returns>
    public TimeSpan? RetryAfter(string token, string ip)
    {
        var now = _time.GetUtcNow();
        var a = Remaining(LinkKey(token), now);
        var b = Remaining(IpKey(ip), now);
        if (a is null || b is null)
        {
            return a ?? b; // a lifted comparison with null is false, which would drop an active lock
        }

        return a > b ? a : b;
    }

    /// <summary>
    /// Records a wrong password.
    /// </summary>
    /// <param name="token">The link token.</param>
    /// <param name="ip">The client IP address.</param>
    /// <param name="maxPerLink">Failures allowed per link before locking.</param>
    /// <param name="maxPerIp">Failures allowed per IP before locking.</param>
    public void Fail(string token, string ip, int maxPerLink, int maxPerIp)
    {
        var now = _time.GetUtcNow();
        Prune(now);
        Record(LinkKey(token), Math.Max(1, maxPerLink), now);
        Record(IpKey(ip), Math.Max(1, maxPerIp), now);
    }

    /// <summary>
    /// Records a right password: clears the link's counter (not the IP's, so one known
    /// password cannot be used to reset guessing on other links).
    /// </summary>
    /// <param name="token">The link token.</param>
    public void Succeed(string token) => _entries.TryRemove(LinkKey(token), out _);

    private static string LinkKey(string token) => "t:" + token;

    private static string IpKey(string ip) => "i:" + ip;

    private TimeSpan? Remaining(string key, DateTimeOffset now)
        => _entries.TryGetValue(key, out var e) && e.LockedUntil > now ? e.LockedUntil - now : null;

    private void Record(string key, int max, DateTimeOffset now)
    {
        _entries.AddOrUpdate(
            key,
            _ => Next(new Entry(0, 0, now, DateTimeOffset.MinValue, now), max, now),
            (_, e) => Next(e, max, now));
    }

    private static Entry Next(Entry e, int max, DateTimeOffset now)
    {
        var failures = now - e.WindowStart > Window ? 0 : e.Failures;
        var windowStart = failures == 0 ? now : e.WindowStart;
        failures++;
        if (failures < max)
        {
            return e with { Failures = failures, WindowStart = windowStart, LastSeen = now };
        }

        var lockouts = e.Lockouts + 1;
        var ticks = Math.Min(MaxLock.Ticks, FirstLock.Ticks << Math.Min(lockouts - 1, 10));
        return new Entry(0, lockouts, now, now + TimeSpan.FromTicks(ticks), now);
    }

    private void Prune(DateTimeOffset now)
    {
        // shortcut: full scan, only once the table is large; fine for a home server.
        if (_entries.Count < 10_000)
        {
            return;
        }

        foreach (var pair in _entries)
        {
            if (now - pair.Value.LastSeen > Forget)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed record Entry(int Failures, int Lockouts, DateTimeOffset WindowStart, DateTimeOffset LockedUntil, DateTimeOffset LastSeen);
}
