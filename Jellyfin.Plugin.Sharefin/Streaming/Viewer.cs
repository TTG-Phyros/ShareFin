using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.Sharefin.Model;

namespace Jellyfin.Plugin.Sharefin.Streaming;

/// <summary>
/// A browser tab watching through a share link: one ffmpeg job, and one slot, per link and viewer.
/// </summary>
public sealed class Viewer
{
    private Viewer(Guid linkId, string id)
    {
        LinkId = linkId;
        Id = id;
    }

    /// <summary>Gets the link the viewer watches through.</summary>
    internal Guid LinkId { get; }

    /// <summary>Gets the viewer id, used as Jellyfin's transcode device id.</summary>
    internal string Id { get; }

    /// <summary>
    /// Identifies the viewer from its address and the random session id its page draws once.
    /// The address stays in the hash, so knowing someone's session id is not enough to stop their stream.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <param name="ip">The client address.</param>
    /// <param name="sid">The session id, or null.</param>
    /// <returns>The viewer, or null when the session id is malformed.</returns>
    public static Viewer? TryCreate(ShareLink link, string ip, string? sid)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (sid is not null && !(sid.Length is > 0 and <= 64 && sid.All(char.IsAsciiLetterOrDigit)))
        {
            return null;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(ip + "|" + sid));
        return new Viewer(link.Id, "sharefin-" + link.Id.ToString("N") + "-" + Convert.ToHexStringLower(hash.AsSpan(0, 8)));
    }
}
