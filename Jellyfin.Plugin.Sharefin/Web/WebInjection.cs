#pragma warning disable SA1402, SA1649 // the hosted service and the callback types it registers, kept together

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Sharefin.Web;

/// <summary>
/// Adds client.js to the Jellyfin web client through the File Transformation plugin, when it is installed.
/// </summary>
public sealed class WebInjection : IHostedService
{
    private static readonly Guid TransformationId = Guid.Parse("8d0f6a4e-3f0b-4c55-9d3e-5b1f2a7c9e41");
    private readonly ILogger<WebInjection> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="WebInjection"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    public WebInjection(ILogger<WebInjection> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            // File Transformation lives in its own load context: it can only be reached by reflection.
            var register = AssemblyLoadContext.All
                .SelectMany(c => c.Assemblies)
                .FirstOrDefault(a => a.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false)
                ?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface")
                ?.GetMethod("RegisterTransformation");
            if (register is null)
            {
                _logger.LogInformation("File Transformation plugin not found: the \"Create share link\" menu entry is disabled");
                return Task.CompletedTask;
            }

            var json = JsonSerializer.Serialize(new
            {
                id = TransformationId,
                fileNamePattern = "index.html",
                callbackAssembly = typeof(IndexPatch).Assembly.FullName,
                callbackClass = typeof(IndexPatch).FullName,
                callbackMethod = nameof(IndexPatch.Patch),
            });

            // The parameter is a Newtonsoft JObject: build it with its own Parse to avoid referencing Newtonsoft.
            var payloadType = register.GetParameters()[0].ParameterType;
            var payload = payloadType.GetMethod("Parse", [typeof(string)])!.Invoke(null, [json]);
            register.Invoke(null, [payload]);
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NullReferenceException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not register the web client script with File Transformation");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// The file sent by File Transformation to <see cref="IndexPatch.Patch"/>.
/// </summary>
public class PatchPayload
{
    /// <summary>Gets or sets the file contents.</summary>
    public string? Contents { get; set; }
}

/// <summary>
/// Called by File Transformation for the web client's index.html.
/// </summary>
public static class IndexPatch
{
    private const string Marker = "data-sharefin";

    private static readonly Lazy<string> _script = new(() =>
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Jellyfin.Plugin.Sharefin.Web.client.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// Adds the Sharefin client script before the closing body tag.
    /// </summary>
    /// <param name="payload">The current index.html.</param>
    /// <returns>The patched index.html.</returns>
    public static string Patch(PatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var html = payload.Contents ?? string.Empty;
        var end = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (end < 0 || html.Contains(Marker, StringComparison.Ordinal))
        {
            return html;
        }

        return html.Insert(end, "<script " + Marker + ">" + _script.Value + "</script>");
    }
}
