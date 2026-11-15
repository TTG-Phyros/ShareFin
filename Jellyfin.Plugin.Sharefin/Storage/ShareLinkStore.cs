using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.Sharefin.Model;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Sharefin.Storage;

/// <summary>
/// JSON file store for share links, kept in memory. Only this class writes the file.
/// </summary>
public sealed class ShareLinkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly Lock _gate = new();
    private readonly string _path;
    private readonly List<ShareLink> _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareLinkStore"/> class.
    /// </summary>
    /// <param name="paths">Jellyfin application paths.</param>
    public ShareLinkStore(IApplicationPaths paths)
        : this(Path.Combine(paths?.DataPath ?? throw new ArgumentNullException(nameof(paths)), "sharefin"))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ShareLinkStore"/> class in a given folder.
    /// </summary>
    /// <param name="directory">Folder holding links.json.</param>
    internal ShareLinkStore(string directory)
    {
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "links.json");

        // A corrupt file must stop the plugin rather than be silently replaced by an empty list on the next write.
        _links = File.Exists(_path)
            ? JsonSerializer.Deserialize<List<ShareLink>>(File.ReadAllBytes(_path), JsonOptions) ?? []
            : [];
    }

    /// <summary>
    /// Gets a snapshot of every link, newest first.
    /// </summary>
    /// <returns>The links.</returns>
    public IReadOnlyList<ShareLink> GetAll()
    {
        lock (_gate)
        {
            return _links.OrderByDescending(l => l.CreatedUtc).ToList();
        }
    }

    /// <summary>
    /// Finds a link by its URL token.
    /// </summary>
    /// <param name="token">The token.</param>
    /// <returns>The link, or null.</returns>
    public ShareLink? GetByToken(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        lock (_gate)
        {
            return _links.Find(l => string.Equals(l.Token, token, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Adds a link and saves.
    /// </summary>
    /// <param name="link">The new link.</param>
    public void Add(ShareLink link)
    {
        lock (_gate)
        {
            _links.Add(link);
            Save();
        }
    }

    /// <summary>
    /// Changes a link and saves.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <param name="change">The change to apply.</param>
    /// <returns>False when no such link exists.</returns>
    public bool Update(Guid id, Action<ShareLink> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        lock (_gate)
        {
            var link = _links.Find(l => l.Id == id);
            if (link is null)
            {
                return false;
            }

            change(link);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Deletes a link and saves.
    /// </summary>
    /// <param name="id">The link id.</param>
    /// <returns>False when no such link exists.</returns>
    public bool Remove(Guid id)
    {
        lock (_gate)
        {
            if (_links.RemoveAll(l => l.Id == id) == 0)
            {
                return false;
            }

            Save();
            return true;
        }
    }

    private void Save()
    {
        // Write then rename, so a crash mid-write never leaves a truncated file.
        var temp = _path + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_links, JsonOptions));
        File.Move(temp, _path, true);
    }
}
