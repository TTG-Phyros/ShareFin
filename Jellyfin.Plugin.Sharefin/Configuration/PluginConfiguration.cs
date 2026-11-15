using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Sharefin.Configuration;

/// <summary>
/// Plugin settings. Share links themselves live in their own JSON store, not here:
/// this object is sent to the dashboard as is and rewritten on every save.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the public address links are copied with (e.g. https://media.example.com),
    /// so an admin browsing from the LAN does not send a local address. Empty: the dashboard's address.
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how many days a remembered password lasts on a permanent link, renewed on each visit.
    /// </summary>
    public int RememberDays { get; set; } = 30;

    /// <summary>
    /// Gets or sets the failed password attempts allowed per link before it locks.
    /// </summary>
    public int MaxFailuresPerLink { get; set; } = 5;

    /// <summary>
    /// Gets or sets the failed password attempts allowed per IP address before it locks.
    /// </summary>
    public int MaxFailuresPerIp { get; set; } = 20;
}
