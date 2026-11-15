using System;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.Sharefin.Security;

/// <summary>
/// Decides which library items a link gives access to: the shared item and its descendants.
/// </summary>
public static class ShareScope
{
    /// <summary>
    /// Whether an item kind can be shared.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>True for movies, series, seasons and episodes.</returns>
    public static bool IsShareable(BaseItem item) => item is MediaBrowser.Controller.Entities.Movies.Movie or Series or Season or Episode;

    /// <summary>
    /// Whether <paramref name="item"/> is <paramref name="root"/> or one of its descendants.
    /// Uses SeriesId/SeasonId rather than the folder tree, so episodes in virtual seasons
    /// (whose parent folder is the series) are handled.
    /// </summary>
    /// <param name="root">The shared item.</param>
    /// <param name="item">The requested item.</param>
    /// <returns>True when access is allowed.</returns>
    public static bool Contains(BaseItem root, BaseItem item)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(item);
        if (item.Id.Equals(root.Id))
        {
            return true;
        }

        return root switch
        {
            Series => (item is Season season && season.SeriesId.Equals(root.Id))
                || (item is Episode episode && episode.SeriesId.Equals(root.Id)),
            Season => item is Episode e && e.SeasonId.Equals(root.Id),
            _ => false,
        };
    }
}
