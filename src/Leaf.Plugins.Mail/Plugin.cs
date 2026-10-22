using System.Reflection;
using Leaf.Sdk;
using Leaf.Sdk.Services;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Leaf.Plugins.Mail;

public sealed class MailPlugin : ILeafPlugin
{
    public const string PluginId = "mail";

    public PluginManifest Manifest { get; } = LoadManifest();

    private static PluginManifest LoadManifest()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("plugin.json")
            ?? throw new InvalidOperationException("Embedded plugin.json is missing");
        using var reader = new StreamReader(stream);
        return PluginManifest.Parse(reader.ReadToEnd());
    }

    public void ConfigureServices(IServiceCollection services, PluginContext ctx)
    {
        services.AddSingleton(new GoogleEndpoints());
        // Plugin-private client: no default headers, no cookies, never shared with other origins.
        services.AddKeyedSingleton(PluginId, (_, _) => new HttpClient(new SocketsHttpHandler
        {
            UseCookies = false,
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        }) { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton(sp => new MailStore(sp.GetRequiredKeyedService<IEntityStore>(PluginId)));
        services.AddSingleton(sp => new GoogleOAuthClient(
            sp.GetRequiredKeyedService<HttpClient>(PluginId), sp.GetRequiredService<GoogleEndpoints>()));
        services.AddSingleton(sp => new MailTokenCache(
            sp.GetRequiredService<GoogleOAuthClient>(), sp.GetRequiredService<MailStore>()));
        services.AddSingleton(sp => new GmailClient(
            sp.GetRequiredKeyedService<HttpClient>(PluginId), sp.GetRequiredService<GoogleEndpoints>(),
            sp.GetRequiredService<MailTokenCache>(), new MailBackoff()));
        services.AddSingleton(sp => new MailLiveOutbox(sp.GetRequiredKeyedService<IPluginEvents>(PluginId)));
        services.AddSingleton(sp => new MailService(
            sp.GetRequiredService<MailStore>(),
            sp.GetRequiredService<MailTokenCache>(),
            sp.GetRequiredService<GmailClient>(),
            sp.GetRequiredService<GoogleOAuthClient>(),
            sp.GetRequiredKeyedService<IRecordStreams>(PluginId),
            sp.GetRequiredService<MailLiveOutbox>(),
            sp.GetService<ILogger<MailService>>()));
        services.AddSingleton(sp => new MailConsent(
            sp.GetRequiredService<MailStore>(),
            sp.GetRequiredService<GoogleOAuthClient>(),
            sp.GetRequiredService<MailService>(),
            sp.GetService<ILogger<MailConsent>>()));
        services.AddSingleton(sp => new MailPoller(
            sp.GetRequiredService<MailService>(), sp.GetService<ILogger<MailPoller>>()));
        services.AddSingleton(sp => new MailNovaExtensions(
            sp.GetRequiredKeyedService<IPluginExtensions>(PluginId),
            sp.GetRequiredService<MailStore>(),
            sp.GetRequiredService<MailLiveOutbox>()));
        services.AddSingleton<IPluginSettingsProvider>(sp => new MailSettingsProvider(sp.GetRequiredService<MailStore>()));
    }

    public void MapEndpoints(RouteGroupBuilder group) => MailEndpoints.Map(group);

    /// <summary>
    /// The host runs this only for an enabled plugin, so the poller and Nova
    /// contributions exist only while Mail is enabled. The poller itself stays dormant
    /// until an account is connected.
    /// </summary>
    public async Task OnStartupAsync(IPluginHost host, CancellationToken ct)
    {
        await host.GetRequiredService<MailStore>().EnsureConfigAsync(ct);
        host.GetRequiredService<MailNovaExtensions>().Start();
        var poller = host.GetRequiredService<MailPoller>();
        host.GetRequiredService<MailConsent>().Connected += poller.Wake;
        var logger = host.GetRequiredService<ILoggerFactory>().CreateLogger("Leaf.Plugins.Mail.Poller");
        _ = Task.Run(async () =>
        {
            try { await poller.RunAsync(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex) { logger.LogError("Mail polling loop terminated ({Type})", ex.GetType().Name); }
        }, ct);
    }
}
