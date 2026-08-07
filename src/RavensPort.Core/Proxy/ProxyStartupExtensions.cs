using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms.Builder;

namespace RavensPort.Core.Proxy;

public static class ProxyStartupExtensions
{
    /// <summary>
    /// Wires up storage, YARP (with an empty initial config — the real routes get loaded once
    /// ConfigStoreCache finishes loading from disk, via ConfigStoreInitializerHostedService),
    /// and the credential-injection transform.
    /// </summary>
    public static IServiceCollection AddRavensPort(this IServiceCollection services)
    {
        services.AddSingleton<ActivityLog>();
        services.AddSingleton<ICliRunner, CliRunner>();

        services.AddSingleton<OnePasswordSession>();

        // Two runners, because there are two ways in. The desktop-app path can only go through the
        // in-process SDK; the service-account path prefers the real op.exe when it is installed, so
        // the token lives in a child process that exits rather than in a library mapped into this
        // one. OnePasswordVaultProvider.RunAsync picks between them per call.
        services.AddSingleton(sp => new OnePasswordVaultProvider(
            new NativeCliRunner(
                sp.GetRequiredService<ActivityLog>(),
                session: sp.GetRequiredService<OnePasswordSession>()),
            sp.GetRequiredService<ActivityLog>(),
            exePathOverride: "native",
            session: sp.GetRequiredService<OnePasswordSession>(),
            processRunner: sp.GetRequiredService<ICliRunner>()));

        services.AddSingleton<ProtonPassSession>();
        services.AddSingleton<ProtonPassInstaller>();
        // Where the Proton Pass session key is kept, and the one registration in this file that
        // differs by platform. Windows binds it to a Hello gesture; the portable build has nowhere
        // to put it yet and says so rather than pretending, so the setup page never offers to keep
        // a session it could not reopen. See ISessionKeyProtector.
        //
        // The saved 1Password service-account token goes the same way and for the same reason, and
        // on Windows it is the same object: HelloKeyProtector holds both, so it is registered once
        // and both interfaces resolve to that instance.
#if WINDOWS
        services.AddSingleton<HelloKeyProtector>();
        services.AddSingleton<ISessionKeyProtector>(sp => sp.GetRequiredService<HelloKeyProtector>());
        services.AddSingleton<IServiceTokenProtector>(sp => sp.GetRequiredService<HelloKeyProtector>());
#else
        services.AddSingleton<ISessionKeyProtector, UnavailableSessionKeyProtector>();
        services.AddSingleton<IServiceTokenProtector, UnavailableServiceTokenProtector>();
#endif

        // Constructed by hand rather than by convention: the provider's exePathOverride parameter
        // is a test seam that takes a string, and letting the container guess at a string is how
        // you end up with a connection string as an executable path.
        services.AddSingleton(sp => new ProtonPassVaultProvider(
            sp.GetRequiredService<ICliRunner>(),
            sp.GetRequiredService<ActivityLog>(),
            exePathOverride: null,
            sp.GetRequiredService<ProtonPassSession>()));

        services.AddSingleton<VaultGateService>();
        services.AddSingleton<ProtonPassAuthenticator>();

        // Not a provider directly: which one is active is not known until the gate has probed both
        // managers, which happens after this container is built. GatedConfigVault forwards to
        // whatever the gate settles on, so ConfigStoreCache can be constructed before the answer
        // exists without capturing the wrong backend for the life of the process.
        services.AddSingleton<IConfigVault, GatedConfigVault>();

        services.AddSingleton<ConfigStoreCache>();
        services.AddHostedService<ConfigStoreInitializerHostedService>();

        // Registered as a singleton first so the UI can read its state and push a sync on demand;
        // AddHostedService<T>() alone would create an instance nothing else could reach.
        services.AddSingleton<VaultSyncQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<VaultSyncQueue>());

        services.AddSingleton<VaultIntegrityService>();

        services.AddSingleton<GoogleOAuthService>();
        services.AddSingleton<OAuth2Service>();
        services.AddSingleton<AccessTokenProvider>();
        services.AddSingleton<CredentialTestService>();

        // Registered as a singleton first, then handed to the hosting layer, so the UI can
        // resolve the same instance and clear a credential's retry backoff after a manual
        // reconnect. AddHostedService<T>() alone would create an instance the UI cannot reach.
        services.AddSingleton<TokenRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<TokenRefreshService>());

        // A crashing background service defaults to tearing down the entire host. For an
        // always-on tray app that is the worst outcome: Kestrel stops, every proxied request
        // starts failing, and the tray icon sits there looking healthy. The refresh loop now
        // handles its own errors per tick, and this makes sure any gap in that cannot take
        // the proxy with it.
        services.Configure<HostOptions>(options =>
            options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

        var configProvider = new InMemoryConfigProvider([], []);
        services.AddSingleton(configProvider);
        services.AddSingleton<IProxyConfigProvider>(configProvider);

        services.AddSingleton<ProxyConfigChangeNotifier>();
        services.AddSingleton<ITransformProvider, CredentialInjectionTransformProvider>();

        services.AddReverseProxy();
        services.AddMcpFunnel();

        return services;
    }

    /// <summary>
    /// Registers the MCP funnel: the upstream session pool, the per-funnel handler factory, and
    /// an MCP server whose behaviour is chosen per request from the slug in the path.
    ///
    /// Stateless is the load-bearing setting. In stateless mode ConfigureSessionOptions runs on
    /// every HTTP request rather than once when a session is created, so a funnel edited in the
    /// GUI takes effect on the agent's next call — no session to invalidate, no list_changed
    /// notification to plumb, and no session affinity to preserve. The cost is that the funnel
    /// endpoint cannot offer sampling, elicitation, or resource subscriptions, none of which a
    /// tool-shaping proxy needs. Upstream sessions are stateful and pooled regardless.
    /// </summary>
    private static IServiceCollection AddMcpFunnel(this IServiceCollection services)
    {
        // Registered here rather than beside Kestrel's own configuration, even though Kestrel is
        // its first reader. The connection pool depends on it — it has to dial this app on the
        // scheme the listener actually answers on — so leaving the registration in the WPF
        // startup path made the pool unresolvable in every host that is not the app itself, the
        // test host included.
        services.AddSingleton<KestrelMtlsState>();

        services.AddSingleton<McpSourceConnectionPool>();
        services.AddSingleton<McpCatalogCache>();
        services.AddSingleton<McpFunnelHandlerFactory>();

        services.AddMcpServer()
            .WithHttpTransport(options =>
            {
                options.Stateless = true;

                options.ConfigureSessionOptions = (httpContext, serverOptions, _) =>
                {
                    var handlerFactory = httpContext.RequestServices.GetRequiredService<McpFunnelHandlerFactory>();

                    // The gate has already refused unknown slugs, so a miss here means the funnel
                    // was deleted between the two — answer as an empty server rather than throw.
                    var slug = httpContext.Request.RouteValues[McpFunnelEndpoints.SlugRouteValue]?.ToString();
                    if (handlerFactory.FindFunnel(slug) is not { } funnel) return Task.CompletedTask;

                    serverOptions.ServerInfo = new Implementation
                    {
                        Name = $"RavensPort funnel: {funnel.Name}",
                        Version = typeof(ProxyStartupExtensions).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                    };

                    // Declared unconditionally. A funnel's sources can gain or lose prompts and
                    // resources at any time, and capabilities are negotiated once per request —
                    // advertising only what happens to exist right now would make a client that
                    // connected a moment earlier believe the funnel can never offer them.
                    serverOptions.Capabilities = new ServerCapabilities
                    {
                        Tools = new ToolsCapability(),
                        Resources = new ResourcesCapability(),
                        Prompts = new PromptsCapability(),
                    };

                    serverOptions.Handlers = handlerFactory.Create(funnel.Id, funnel.Name);

                    return Task.CompletedTask;
                };
            });

        return services;
    }
}
