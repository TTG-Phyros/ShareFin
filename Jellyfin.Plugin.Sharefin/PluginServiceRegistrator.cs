using Jellyfin.Plugin.Sharefin.Configuration;
using Jellyfin.Plugin.Sharefin.Content;
using Jellyfin.Plugin.Sharefin.Party;
using Jellyfin.Plugin.Sharefin.Security;
using Jellyfin.Plugin.Sharefin.Storage;
using Jellyfin.Plugin.Sharefin.Streaming;
using Jellyfin.Plugin.Sharefin.Web;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
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
        serviceCollection.AddSingleton<ShareGate>();
        serviceCollection.AddSingleton<ShareContent>();
        serviceCollection.AddSingleton<WatchParties>();
        serviceCollection.AddSingleton(services => new SharePlayback(
            new ShareTranscoder(
                services.GetRequiredService<IMediaSourceManager>(),
                services.GetRequiredService<ITranscodeManager>(),
                services.GetRequiredService<EncodingHelper>(),
                services.GetRequiredService<IServerConfigurationManager>()),
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration()));
        serviceCollection.AddHostedService<WebInjection>();
    }
}
