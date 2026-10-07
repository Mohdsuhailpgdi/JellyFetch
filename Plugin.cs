using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.JellyFetch.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.JellyFetch;

/// <summary>
/// The main plugin class. On startup it auto-patches the Jellyfin web directory
/// by writing jellyfetch-inject.js and adding a &lt;script&gt; tag to index.html,
/// so users get the full Download UI without any manual installation steps.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private const string InjectScriptName = "jellyfetch-inject.js";
    private const string ScriptMarker    = "jellyfetch-inject.js"; // used to check if already patched
    private readonly IApplicationPaths   _applicationPaths;
    private readonly ILogger<Plugin>     _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="xmlSerializer">Instance of the <see cref="IXmlSerializer"/> interface.</param>
    /// <param name="loggerFactory">Instance of the <see cref="ILoggerFactory"/> interface.</param>
    public Plugin(
        IApplicationPaths applicationPaths,
        IXmlSerializer xmlSerializer,
        ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance          = this;
        _applicationPaths = applicationPaths;
        _logger           = loggerFactory.CreateLogger<Plugin>();
    }

    /// <inheritdoc />
    public override string Name => "JellyFetch";

    /// <inheritdoc />
    public override Guid Id => Guid.Parse("A1B2C3D4-E5F6-7890-1234-567890ABCDEF");

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin Instance { get; private set; }

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "Downloaders",
                DisplayName = "JellyFetch",
                EnableInMainMenu = true,
                MenuSection = "plugins",
                MenuIcon = "cloud_download",
                EmbeddedResourcePath = GetType().Namespace + ".Web.downloaders.html"
            },
            new PluginPageInfo
            {
                Name = "downloadersjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.downloaders.js"
            }
        };
    }

    // ─── In-Memory UI Injection ─────────────────────────────────────────────

    /// <summary>
    /// Registers the startup filter that injects our HTTP middleware into the ASP.NET Core pipeline.
    /// This entirely replaces the old index.html file-patching mechanism, preventing Linux permission lockouts.
    /// </summary>
    public class PluginServiceRegistrator : MediaBrowser.Controller.Plugins.IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection services, MediaBrowser.Controller.IServerApplicationHost applicationHost)
        {
            services.AddTransient<IStartupFilter, JellyFetchStartupFilter>();
        }
    }

    public class JellyFetchStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return builder =>
            {
                builder.UseMiddleware<JellyFetchInjectionMiddleware>();
                next(builder);
            };
        }
    }

    public class JellyFetchInjectionMiddleware
    {
        private readonly RequestDelegate _next;

        public JellyFetchInjectionMiddleware(RequestDelegate next)
        {
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;
            // Only intercept the root web index
            if (!path.Equals("/web/index.html", StringComparison.OrdinalIgnoreCase) && 
                !path.Equals("/web/", StringComparison.OrdinalIgnoreCase))
            {
                await _next(context);
                return;
            }

            // Remove Content-Length right before headers are sent so Kestrel switches to chunked encoding.
            // This allows us to modify the HTML body size dynamically.
            context.Response.OnStarting(() =>
            {
                if (context.Response.StatusCode == 200 && 
                    context.Response.ContentType != null && 
                    context.Response.ContentType.Contains("text/html"))
                {
                    context.Response.Headers.Remove("Content-Length");
                }
                return Task.CompletedTask;
            });

            var originalBodyStream = context.Response.Body;
            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            await _next(context);

            context.Response.Body = originalBodyStream;

            if (context.Response.StatusCode == 200 && 
                context.Response.ContentType != null && 
                context.Response.ContentType.Contains("text/html"))
            {
                responseBody.Seek(0, SeekOrigin.Begin);
                var html = await new StreamReader(responseBody).ReadToEndAsync();

                // Inject our script tag (which is dynamically served by DownloadersController)
                string scriptTag = $"\n    <script src=\"/System/Configuration/Downloaders/Inject.js?v={Plugin.Instance.Version}\"></script>\n";

                if (!html.Contains("Downloaders/Inject.js"))
                {
                    if (html.Contains("</body>"))
                    {
                        html = html.Replace("</body>", scriptTag + "</body>");
                    }
                    else
                    {
                        html += scriptTag;
                    }
                }

                var injectedBytes = Encoding.UTF8.GetBytes(html);
                await context.Response.Body.WriteAsync(injectedBytes, 0, injectedBytes.Length);
            }
            else
            {
                responseBody.Seek(0, SeekOrigin.Begin);
                await responseBody.CopyToAsync(originalBodyStream);
            }
        }
    }
}
