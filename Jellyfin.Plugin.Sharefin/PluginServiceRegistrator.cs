using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using Jellyfin.Plugin.Sharefin.Streaming;
using Jellyfin.Plugin.Sharefin.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Sharefin;

/// <summary>
/// Registers the plugin services.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<ShareLinkStore>();
        serviceCollection.AddSingleton<ShareCookie>();
        serviceCollection.AddSingleton<LoginThrottle>();
        serviceCollection.AddSingleton<ShareTranscoder>();
        serviceCollection.AddHostedService<WebInjection>();
    }
}
