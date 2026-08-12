using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// The real app's pipeline — guard, funnel gate, funnel endpoints, YARP — on a loopback socket,
/// with storage and logs pointed at temp paths so a test run cannot touch the %APPDATA% store
/// belonging to whoever runs the suite.
/// </summary>
internal sealed class FunnelTestHost : IAsyncDisposable
{
    // A fixture the test host hands to itself, not a credential.
    public const string ApiKey = "funnel-test-key-0123456789"; // gitleaks:allow

    private readonly WebApplication _proxy;
    private readonly string _logPath;
    private readonly List<McpClient> _clients = [];

    private FunnelTestHost(WebApplication proxy, string baseUrl, string logPath)
    {
        _proxy = proxy;
        BaseUrl = baseUrl;
        _logPath = logPath;
    }

    public string BaseUrl { get; }

    /// <summary>YARP's own explanation for the most recent failed forward, if there was one.</summary>
    public static string? LastForwarderError;

    public ConfigStoreCache Cache => _proxy.Services.GetRequiredService<ConfigStoreCache>();

    public McpSourceConnectionPool Pool => _proxy.Services.GetRequiredService<McpSourceConnectionPool>();

    public ActivityLog ActivityLog => _proxy.Services.GetRequiredService<ActivityLog>();

    /// <summary>
    /// Starts the pipeline. With <paramref name="mtls"/> the listener is the app's mTLS one:
    /// https, a client certificate demanded on every connection, and the funnel's hop into its own
    /// routes having to satisfy that demand like any other caller.
    /// </summary>
    public static async Task<FunnelTestHost> StartAsync(bool mtls = false)
    {
        var logPath = Path.Combine(Path.GetTempPath(), $"ravensport-funnel-logs-{Guid.NewGuid()}");
        // Not excluded from the store build the way the mTLS suites are: plenty of funnel tests use
        // this host without mTLS, and they still have to build. Only the branch that mints goes,
        // because MtlsCertificateFactory.GenerateClientCertificatePfx is not there. See BuildProfile.
#if STORE_BUILD
        if (mtls) throw new NotSupportedException("mTLS is not part of the Microsoft Store build.");
        string? pfx = null;
#else
        var pfx = mtls ? MtlsCertificateFactory.GenerateClientCertificatePfx() : null;
#endif

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"{(mtls ? "https" : "http")}://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddRavensPort();
        builder.Services.Replace(ServiceDescriptor.Singleton<IConfigVault>(_ => new InMemoryVault()));
        builder.Services.Replace(ServiceDescriptor.Singleton(_ => new ActivityLog(logPath)));

        // Deliberately the same shape as App.StartHost: the callback reads the state when the
        // endpoint is bound, which is inside StartAsync, so the certificate below is in place by
        // then even though it is not yet loaded here.
        builder.WebHost.ConfigureKestrel(options => options.ConfigureHttpsDefaults(https =>
        {
            var state = options.ApplicationServices.GetRequiredService<KestrelMtlsState>();
            if (state.Certificate is not { } certificate) return;

            https.ServerCertificate = certificate;
            https.ClientCertificateMode = Microsoft.AspNetCore.Server.Kestrel.Https.ClientCertificateMode.RequireCertificate;
            https.ClientCertificateValidation = (clientCert, _, _) =>
                string.Equals(clientCert.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase);
        }));

        var proxy = builder.Build();

        if (pfx is not null) proxy.Services.GetRequiredService<KestrelMtlsState>().Enable(pfx);

        // A forwarding failure surfaces as a bare 502 with no detail, which is nearly impossible
        // to tell from an upstream that fell over. YARP records the real reason here.
        proxy.Use(async (context, next) =>
        {
            await next();
            if (context.Features.Get<Yarp.ReverseProxy.Forwarder.IForwarderErrorFeature>() is { } error)
            {
                LastForwarderError = $"{error.Error}: {error.Exception}";
            }
        });

        // Deliberately the same order as App.StartHost; the gate has to sit behind the guard and
        // the funnel endpoints ahead of the catch-all proxy routes.
        proxy.UseLocalAccessGuard();
        proxy.UseMcpFunnelGate();
        proxy.MapMcpFunnel();
        proxy.MapReverseProxy();

        await proxy.StartAsync();

        var baseUrl = proxy.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        var host = new FunnelTestHost(proxy, baseUrl, logPath);
        await host.Cache.MutateAsync(store =>
        {
            store.Settings.McpFunnelEnabled = true;
            store.Settings.MtlsEnabled = mtls;
            store.Settings.MtlsClientCertificatePfx = pfx ?? "";

            // A route-backed source is dialled at 127.0.0.1:{ListenPort}, so the stored port has
            // to match the port actually bound. In the app they are the same by construction —
            // Kestrel is told to listen on the stored value — but here the host takes an
            // ephemeral port, and without this the funnel would quietly dial 5559 instead.
            store.Settings.ListenPort = new Uri(baseUrl).Port;
        });

        return host;
    }

    /// <summary>Registers a source pointing at a no-auth MCP server URL.</summary>
    public async Task<McpSourceRecord> AddRemoteSourceAsync(string alias, string url)
    {
        var source = new McpSourceRecord
        {
            Name = $"source-{alias}",
            Alias = alias,
            Kind = McpSourceKind.RemoteUrl,
            Url = url,
            // Pinned rather than AutoDetect so a test failure means the funnel is broken, not
            // that transport probing raced.
            Transport = McpTransportPreference.StreamableHttp,
        };

        await Cache.MutateAsync(store => store.McpSources.Add(source));
        return source;
    }

    /// <summary>Registers a source reached through one of this proxy's own credentialed routes.</summary>
    public async Task<McpSourceRecord> AddRouteSourceAsync(string alias, Guid routeId)
    {
        var source = new McpSourceRecord
        {
            Name = $"source-{alias}",
            Alias = alias,
            Kind = McpSourceKind.ProxyRoute,
            RouteId = routeId,
            Transport = McpTransportPreference.StreamableHttp,
        };

        await Cache.MutateAsync(store => store.McpSources.Add(source));
        return source;
    }

    public async Task<McpFunnelRecord> AddFunnelAsync(string slug, params McpFunnelSource[] sources)
    {
        var funnel = new McpFunnelRecord
        {
            Name = slug,
            Slug = slug,
            Sources = [.. sources],
        };

        await MutateAsync(store => store.McpFunnels.Add(funnel));
        return funnel;
    }

    /// <summary>
    /// Applies a store edit and then gives every route and funnel that still has no proxy key the
    /// one constant these tests authenticate with.
    ///
    /// Keys are per endpoint in production, and <see cref="LocalAccessGuardTests"/> is where that
    /// isolation is pinned. Here the subject is the funnel's behaviour, so a test that adds a
    /// route should not also have to invent and thread through a key for it — every endpoint on
    /// this host answers to <see cref="ApiKey"/>.
    /// </summary>
    public Task MutateAsync(Action<ConfigStore> mutate) => Cache.MutateAsync(store =>
    {
        mutate(store);

        foreach (var key in store.Routes.Select(r => r.Key).Concat(store.McpFunnels.Select(f => f.Key)))
        {
            if (!key.IsConfigured) key.Value = ApiKey;
        }
    });

    /// <summary>
    /// Pushes route changes into YARP. Funnel edits need no equivalent — the funnel reads config
    /// per request — but a route-backed source is only reachable once its route is live.
    /// </summary>
    public void RebuildProxyConfig() =>
        _proxy.Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();

    /// <summary>
    /// Connects an MCP client to one funnel endpoint, exactly as an agent would — including
    /// presenting the client certificate when the host is running mTLS, which is the only way any
    /// caller gets past the handshake.
    /// </summary>
    public async Task<McpClient> ConnectAsync(string slug, string? apiKey = ApiKey)
    {
        var headers = new Dictionary<string, string>();
        if (apiKey is not null) headers[LocalAccessGuard.ApiKeyHeaderName] = apiKey;

        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri($"{BaseUrl}{McpFunnelEndpoints.BasePath}/{slug}"),
            TransportMode = HttpTransportMode.StreamableHttp,
            ConnectionTimeout = TimeSpan.FromSeconds(30),
            AdditionalHeaders = headers,
        };

        var transport = CreateClientHandler() is { } handler
            ? new HttpClientTransport(options, new HttpClient(handler), null, ownsHttpClient: true)
            : new HttpClientTransport(options);

        var client = await McpClient.CreateAsync(transport);
        _clients.Add(client);

        return client;
    }

    /// <summary>
    /// An outside caller's TLS setup: the client certificate this host demands, and the pinning
    /// that stands in for a chain neither end has. Null when the host is plain HTTP, where the
    /// default handler is what an agent would use.
    /// </summary>
    public SocketsHttpHandler? CreateClientHandler()
    {
        var state = _proxy.Services.GetRequiredService<KestrelMtlsState>();
        if (state.Certificate is not { } certificate) return null;

        return new SocketsHttpHandler
        {
            SslOptions = new System.Net.Security.SslClientAuthenticationOptions
            {
                ClientCertificates = [certificate],
                RemoteCertificateValidationCallback = (_, presented, _, _) =>
                    string.Equals(presented?.GetCertHashString(), certificate.Thumbprint, StringComparison.OrdinalIgnoreCase),
            },
        };
    }

    /// <summary>Calls a tool and returns the raw result, so a caller can inspect IsError.</summary>
    public static async Task<CallToolResult> CallAsync(McpClient client, string tool, string? value = null)
    {
        var arguments = new Dictionary<string, object?>();
        if (value is not null) arguments["value"] = value;

        return await client.CallToolAsync(tool, arguments!);
    }

    /// <summary>
    /// Calls a tool that is expected to succeed and returns its text. Asserts on IsError rather
    /// than letting it slide, because the SDK reports a refused or failed call as a result with
    /// IsError set — not as an exception — so an unasserted failure would otherwise show up as a
    /// confusing string mismatch further down the test.
    /// </summary>
    public static async Task<string> CallTextAsync(McpClient client, string tool, string? value = null)
    {
        var result = await CallAsync(client, tool, value);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "";

        Assert.True(result.IsError != true, $"'{tool}' failed: {text}");

        return text;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
        {
            try { await client.DisposeAsync(); } catch { /* best effort */ }
        }

        await _proxy.StopAsync();
        await _proxy.DisposeAsync();


        try { Directory.Delete(_logPath, recursive: true); } catch { /* best effort */ }
    }
}
