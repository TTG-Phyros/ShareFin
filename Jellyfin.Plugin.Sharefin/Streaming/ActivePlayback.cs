using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// A started playback. Disposing it frees the viewer's slot and stops the ffmpeg job, once.
/// </summary>
public sealed class ActivePlayback : IAsyncDisposable
{
    private readonly Func<Task> _release;
    private int _released;

    internal ActivePlayback(Stream stream, Func<Task> release)
    {
        Stream = stream;
        _release = release;
    }

    /// <summary>Gets the fragmented MP4 stream.</summary>
    public Stream Stream { get; }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _released, 1) == 1)
        {
            return;
        }

        await Stream.DisposeAsync().ConfigureAwait(false);
        await _release().ConfigureAwait(false);
    }
}
