using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Mcp;
using RavensPort.Core.Models;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;
using RavensPort.Core.Vault;

namespace RavensPort.Core.Admin;

/// <summary>
/// The admin API: everything the command line can ask of a running RavensPort.
///
/// Each edit goes through <see cref="ConfigStoreCache.MutateAsync"/> and the same validators and
/// <see cref="ConfigStoreEdits"/> rules the tabs use, so a change made here is indistinguishable
/// from one made in the window — it is queued for the vault the same way, and refused for the same
/// reasons.
///
/// Records are addressed by id or by what a person would type: a credential or upstream by name,
/// a route by its path prefix, a source by alias, a funnel or bridge by slug.
/// </summary>
internal static class AdminEndpoints
{
    internal sealed record Context(IServiceProvider Services, string HostDescription, Action Changed)
    {
        public ConfigStoreCache Cache => Services.GetRequiredService<ConfigStoreCache>();
        public ConfigStore Store => Cache.Current;
        public VaultGateService Gate => Services.GetRequiredService<VaultGateService>();
        public McpSourceConnectionPool Pool => Services.GetRequiredService<McpSourceConnectionPool>();
        public McpCatalogCache Catalogs => Services.GetRequiredService<McpCatalogCache>();
        public ActivityLog? Log => Services.GetService<ActivityLog>();

        public void RebuildProxy() => Services.GetRequiredService<ProxyConfigChangeNotifier>().Rebuild();
    }

    private const string ReadOnlyWarning =
        "Read-only session: this change is held in memory and discarded when RavensPort exits.";

    public static void Map(WebApplication app, Context c)
    {
        var admin = app.MapGroup("/admin");

        admin.MapGet("/status", () => Results.Ok(Status(c)));

        // Everything else needs a configuration to act on. The desktop app answers this socket
        // from the moment it starts, which can be long before anyone has connected a vault.
        var store = admin.MapGroup("").AddEndpointFilter(async (context, next) =>
            c.Cache.IsInitialized
                ? await next(context)
                : Results.Conflict(new AdminMessage(
                    "RavensPort is running but has not loaded a configuration yet — connect a password manager first.")));

        store.MapPost("/reload", (ReloadRequest? request) => ReloadAsync(c, request ?? new ReloadRequest()));

        MapCredentials(store, c);
        MapUpstreams(store, c);
        MapRoutes(store, c);
        MapSources(store, c);
        MapFunnels(store, c);
        MapBridges(store, c);
        MapSettings(store, c);
    }

    // ---- status and reload ----------------------------------------------------------------------

    private static AdminStatus Status(Context c)
    {
        var store = c.Store;
        var queue = c.Services.GetService<VaultSyncQueue>();

        return new AdminStatus(
            Backend: c.Gate.Status.Selected.ToString(),
            VaultName: c.Gate.Selected.VaultName,
            ReadOnly: c.Gate.IsReadOnly,
            ListenPort: store.Settings.ListenPort,
            Scheme: c.Services.GetRequiredService<KestrelMtlsState>().Scheme,
            McpFunnelEnabled: store.Settings.McpFunnelEnabled,
            McpApiBridgeEnabled: store.Settings.McpApiBridgeEnabled,
            MtlsEnabled: store.Settings.MtlsEnabled,
            PendingChanges: c.Cache.HasPendingChanges,
            SyncState: queue?.State.ToString() ?? "Unknown",
            Credentials: store.Credentials.Count,
            Upstreams: store.Upstreams.Count,
            Routes: store.Routes.Count,
            Sources: store.McpSources.Count,
            Funnels: store.McpFunnels.Count,
            Bridges: store.McpApiBridges.Count,
            Host: c.HostDescription,
            Version: typeof(AdminEndpoints).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
    }

    private static async Task<IResult> ReloadAsync(Context c, ReloadRequest request)
    {
        if (c.Cache.HasPendingChanges && !request.Force)
        {
            return Results.Conflict(new AdminMessage(
                c.Gate.IsReadOnly
                    ? "There are changes held in memory, and reloading replaces them with what is in the vault. Pass --force to discard them."
                    : "There are changes not yet saved to the vault, and reloading would discard them. Wait for them to save, or pass --force."));
        }

        // A PAT session may have expired since the last vault call; this is the first one in a
        // while, most likely, because a read-only session never writes.
        if (c.Services.GetService<ProtonPassPatSession>() is { HasToken: true } pat)
        {
            await pat.EnsureLoggedInAsync();
        }

        await c.Cache.ReloadAsync();
        c.RebuildProxy();
        await c.Pool.InvalidateAllAsync();
        c.Changed();

        return Results.Ok(new AdminMessage("Reloaded the configuration from the vault."));
    }

    // ---- credentials ----------------------------------------------------------------------------

    private static void MapCredentials(RouteGroupBuilder g, Context c)
    {
        g.MapGet("/credentials", () => Results.Ok(c.Store.Credentials.Select(cred => new CredentialSummary(
            cred.Id, cred.Name, cred.Kind, cred.Token is not null,
            cred.Kind == CredentialKind.ApiKey ? "static key" : cred.Token?.DescribeExpiry() ?? "not connected",
            cred.NeedsReconnect))));

        // The record itself, as the vault holds it, so every kind's fields are settable without
        // a request shape per kind. A token cannot be supplied — that is what sign-in is for.
        g.MapPost("/credentials", async (CredentialRecord record) =>
        {
            record.Token = null;
            record.NeedsReconnect = false;
            record.Name = record.Name.Trim();

            if (record.Name.Length == 0) return Bad("Credential name is required.");
            if (c.Store.Credentials.Any(x => x.Id == record.Id)) return Bad("A credential with that id already exists.");
            if (FindCredential(c.Store, record.Name) is not null)
            {
                return Bad($"A credential named '{record.Name}' already exists. Names must be unique so commands can address them.");
            }

            // Both, always: which checks apply is decided inside the validators from the kind, never
            // by a branch here that a request could steer around.
            if ((CredentialValidation.Validate(record) ?? CredentialValidation.ValidateProviderFields(record)) is { } error)
            {
                return Bad(error);
            }

            await c.Cache.MutateAsync(s => s.Credentials.Add(record));
            c.Changed();

            return Ok(c, record.IsSelfIssuing || record.Kind == CredentialKind.ApiKey
                ? $"Credential '{record.Name}' added."
                : $"Credential '{record.Name}' added — run `ravensport credentials signin \"{record.Name}\"` to connect it.");
        });

        g.MapDelete("/credentials/{key}", async (string key) =>
        {
            if (FindCredential(c.Store, key) is not { } cred) return NotFound("credential", key);

            var used = c.Store.Routes.Count(r => r.Credentials.Any(rc => rc.CredentialId == cred.Id));

            await c.Cache.MutateAsync(s => s.Credentials.Remove(cred));
            c.RebuildProxy();
            c.Changed();

            return Ok(c, used == 0
                ? $"Credential '{cred.Name}' deleted."
                : $"Credential '{cred.Name}' deleted — {used} route(s) still name it and will not be served until it is replaced.");
        });

        g.MapPost("/credentials/{key}/test", async (string key, CancellationToken ct) =>
        {
            if (FindCredential(c.Store, key) is not { } cred) return NotFound("credential", key);

            var result = await c.Services.GetRequiredService<CredentialTestService>().TestAsync(cred, ct);
            return Results.Ok(new CredentialTestReply(result.Success, result.StatusCode, result.Message));
        });

        g.MapPost("/credentials/{key}/signout", async (string key) =>
        {
            if (FindCredential(c.Store, key) is not { } cred) return NotFound("credential", key);

            await c.Cache.MutateAsync(_ =>
            {
                cred.Token = null;
                cred.NeedsReconnect = false;
            });
            c.Changed();

            return Ok(c, $"'{cred.Name}' disconnected — stored token cleared (not revoked at the provider).");
        });

        g.MapPost("/credentials/{key}/signin", (string key, HttpContext http) => SignInAsync(c, key, http));
    }

    /// <summary>
    /// Runs a credential's sign-in and streams what the person has to do as plain text lines, then
    /// one final line: <c>DONE: …</c> or <c>FAILED: …</c>.
    ///
    /// Streamed because the flows that need a person only finish <em>because</em> the person acted
    /// on what they were shown — a device code reported at the end would be reported too late.
    /// </summary>
    private static async Task SignInAsync(Context c, string key, HttpContext http)
    {
        if (FindCredential(c.Store, key) is not { } cred)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            await http.Response.WriteAsJsonAsync(
                new AdminMessage($"No credential '{key}'."), AdminChannel.JsonOptions, http.RequestAborted);
            return;
        }

        http.Response.ContentType = "text/plain; charset=utf-8";
        var writeLock = new SemaphoreSlim(1, 1);
        var aborted = http.RequestAborted;

        async Task Line(string text)
        {
            await writeLock.WaitAsync(aborted);
            try
            {
                await http.Response.WriteAsync(text + "\n", aborted);
                await http.Response.Body.FlushAsync(aborted);
            }
            finally
            {
                writeLock.Release();
            }
        }

        // Interactive OAuth would open a browser on this machine, which a server does not have.
        // The URL goes to the terminal instead, with what it takes to complete it from elsewhere.
        using var redirect = cred.Kind == CredentialKind.OAuth2
            ? BrowserLauncher.Redirect(uri =>
            {
                var callback = new Uri(cred.IsGoogleProvider ? GoogleOAuthService.RedirectUri : LoopbackBrowser.StaticRedirectUri);
                Line($"Open this URL in a browser and approve the request:\n  {uri}\n"
                     + $"It redirects to {callback.GetLeftPart(UriPartial.Authority)} on the machine running RavensPort. "
                     + $"From another machine, forward that port first: ssh -L {callback.Port}:127.0.0.1:{callback.Port} <this-host>")
                    .GetAwaiter().GetResult();
            })
            : null;

        var prompt = new InlineProgress<DeviceCodePrompt>(p =>
            Line($"Enter code {p.UserCode} at {p.VerificationUri} (expires {p.ExpiresAtUtc.ToLocalTime():t}). Waiting for approval…")
                .GetAwaiter().GetResult());

        await Line(cred.Kind switch
        {
            CredentialKind.DeviceCode => $"Asking the provider for a code for '{cred.Name}'…",
            CredentialKind.OAuth2 => $"Starting the browser sign-in for '{cred.Name}'…",
            _ => $"Requesting a token for '{cred.Name}'…",
        });

        c.Services.GetRequiredService<TokenRefreshService>().ResetBackoff(cred);

        try
        {
            // Not inside MutateAsync, for the reason the Credentials tab gives: the flow mutates
            // the record over minutes, and holding the store's lock that long would stall the
            // refresh loop and every other save.
            var outcome = await c.Services.GetRequiredService<OAuth2Service>()
                .StartAuthorizationAsync(cred, prompt, http.RequestAborted);

            if (outcome.Success)
            {
                // Not cancellable on purpose: the token has been issued, and a terminal closed a
                // moment too soon must not cost the person the sign-in they just completed.
                await c.Cache.SaveAsync(CancellationToken.None);
                c.Changed();
                c.Log?.Log($"CONNECT '{cred.Name}' OK via the admin API — token stored");
                await Line($"DONE: '{cred.Name}' connected."
                           + (c.Gate.IsReadOnly ? $" {ReadOnlyWarning}" : ""));
            }
            else
            {
                await Line($"FAILED: {outcome.Error} {outcome.ErrorDescription}".TrimEnd());
            }
        }
        catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
        {
            // The terminal went away; there is nobody to tell.
        }
        catch (Exception ex)
        {
            c.Log?.LogError($"CONNECT '{cred.Name}' via the admin API threw", ex);
            await Line($"FAILED: {ex.Message}");
        }
    }

    /// <summary>Reports on the calling thread, in order — <see cref="Progress{T}"/> posts and can reorder.</summary>
    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    // ---- upstreams ------------------------------------------------------------------------------

    private static void MapUpstreams(RouteGroupBuilder g, Context c)
    {
        g.MapGet("/upstreams", () => Results.Ok(c.Store.Upstreams.Select(u =>
            new UpstreamSummary(u.Id, u.Name, u.BaseUrl, c.Store.Routes.Count(r => r.UpstreamId == u.Id)))));

        g.MapPost("/upstreams", async (AddUpstreamRequest request) =>
        {
            var name = request.Name.Trim();
            var baseUrl = request.BaseUrl.Trim().TrimEnd('/');

            if (name.Length == 0) return Bad("Upstream name is required.");
            if (FindUpstream(c.Store, name) is not null) return Bad($"An upstream named '{name}' already exists.");

            // The access token is attached to every request forwarded here, so a plain-http
            // upstream would put it on the wire in cleartext.
            if (UrlValidation.ValidateEndpoint(baseUrl, "Upstream base URL") is { } error) return Bad(error);

            var upstream = new UpstreamRecord { Name = name, BaseUrl = baseUrl };
            await c.Cache.MutateAsync(s => s.Upstreams.Add(upstream));
            c.RebuildProxy();
            c.Changed();

            return Ok(c, $"Upstream '{name}' added.");
        });

        g.MapDelete("/upstreams/{key}", async (string key) =>
        {
            if (FindUpstream(c.Store, key) is not { } upstream) return NotFound("upstream", key);

            var affected = c.Store.Routes.Count(r => r.UpstreamId == upstream.Id);

            await c.Cache.MutateAsync(s => s.Upstreams.Remove(upstream));
            c.RebuildProxy();
            c.Changed();

            return Ok(c, affected == 0
                ? $"Upstream '{upstream.Name}' deleted."
                : $"Upstream '{upstream.Name}' deleted — {affected} route(s) now have no upstream and will not be served.");
        });
    }

    // ---- routes ---------------------------------------------------------------------------------

    private static void MapRoutes(RouteGroupBuilder g, Context c)
    {
        g.MapGet("/routes", () => Results.Ok(c.Store.Routes.Select(r => new RouteSummary(
            r.Id,
            r.PathPrefix,
            c.Store.Upstreams.FirstOrDefault(u => u.Id == r.UpstreamId)?.Name ?? "(missing)",
            r.Enabled,
            r.StripPrefix,
            r.DescribeCredentials(id => c.Store.Credentials.FirstOrDefault(x => x.Id == id)?.Name),
            r.Key.DescribeExpiry(DateTimeOffset.UtcNow)))));

        g.MapPost("/routes", async (AddRouteRequest request) =>
        {
            var prefix = request.PathPrefix.Trim();
            if (!prefix.StartsWith('/')) prefix = "/" + prefix;

            if (RouteValidation.ValidatePathPrefix(prefix) is { } prefixError) return Bad(prefixError);
            if (ConfigStoreEdits.ValidateUniquePrefix(c.Store, prefix) is { } duplicate) return Bad(duplicate);
            if (FindUpstream(c.Store, request.Upstream) is not { } upstream) return NotFound("upstream", request.Upstream);

            List<RouteCredential> credentials = [];

            if (request.Credential is { Length: > 0 } credentialKey)
            {
                if (FindCredential(c.Store, credentialKey) is not { } cred) return NotFound("credential", credentialKey);

                credentials.Add(new RouteCredential
                {
                    CredentialId = cred.Id,
                    Placement = request.Placement ?? cred.DefaultPlacement,
                    ParameterName = (request.ParameterName ?? cred.DefaultParameterName).Trim(),
                    ValuePrefix = request.ValuePrefix ?? cred.DefaultValuePrefix,
                });
            }

            // Shared with ProxyConfigBuilder, which drops a route whose credential settings cannot
            // be put on the wire — accepting one here would create a route that never serves.
            if (RouteValidation.ValidateCredentials(credentials) is { } injectionError) return Bad(injectionError);

            var route = new RouteMapping
            {
                PathPrefix = prefix,
                UpstreamId = upstream.Id,
                StripPrefix = request.StripPrefix,
                Enabled = true,
                Credentials = credentials,
                Key = ProxyKey.Generate(Lifetime(request.KeyLifetimeDays)),
            };

            await c.Cache.MutateAsync(s => s.Routes.Add(route));
            c.RebuildProxy();
            c.Changed();

            return Ok(c, $"Route '{prefix}' added with its own proxy key ({route.Key.DescribeExpiry(DateTimeOffset.UtcNow)}) — "
                         + $"show it with `ravensport routes key show {prefix}`.");
        });

        // Addressed by id in practice: a prefix is a path, and its slashes do not survive as one
        // URL segment, so the CLI looks the id up from the list first. A prefix without slashes
        // in the middle still works here.
        g.MapDelete("/routes/{key}", async (string key) =>
        {
            if (FindRoute(c.Store, key) is not { } route) return NotFound("route", key);

            await c.Cache.MutateAsync(s => s.Routes.Remove(route));
            c.RebuildProxy();
            await InvalidateSourcesForRouteAsync(c, route.Id);
            c.Changed();

            return Ok(c, $"Route '{route.PathPrefix}' deleted.");
        });

        MapToggle(g, c, "routes", key => FindRoute(c.Store, key), r => r.PathPrefix, (r, on) => r.Enabled = on, rebuild: true);
        MapKey(g, c, "routes", key => FindRoute(c.Store, key), r => r.PathPrefix, r => r.Key,
            r => InvalidateSourcesForRouteAsync(c, r.Id));
    }

    /// <summary>
    /// A funnel source reaching a route over the proxy carries that route's key in headers fixed at
    /// connect time, so a rotated or deleted route would otherwise keep a stale session alive.
    /// </summary>
    private static async Task InvalidateSourcesForRouteAsync(Context c, Guid routeId)
    {
        foreach (var source in c.Store.McpSources.Where(s => s.Kind == McpSourceKind.ProxyRoute && s.RouteId == routeId).ToList())
        {
            await c.Pool.InvalidateSourceAsync(source.Id);
        }
    }

    // ---- MCP sources ----------------------------------------------------------------------------

    private static void MapSources(RouteGroupBuilder g, Context c)
    {
        g.MapGet("/sources", () => Results.Ok(c.Store.McpSources.Select(s => new SourceSummary(
            s.Id, s.Name, s.Alias, s.Kind, DescribeTarget(c.Store, s), s.Enabled,
            c.Catalogs.Get(s.Id) is { } catalog
                ? catalog.Error is null ? catalog.Describe() : $"unreachable — {catalog.Detail}"
                : "not checked yet"))));

        g.MapPost("/sources", async (AddSourceRequest request) =>
        {
            var name = request.Name.Trim();
            if (name.Length == 0) return Bad("Source name is required.");

            var alias = string.IsNullOrWhiteSpace(request.Alias) ? ConfigStoreEdits.DefaultAlias(name) : request.Alias.Trim();
            if (McpFunnelValidation.ValidateAlias(alias, c.Store.McpSources) is { } aliasError) return Bad(aliasError);

            var routeId = Guid.Empty;
            if (request.Route is { Length: > 0 } routeKey)
            {
                if (FindRoute(c.Store, routeKey) is not { } route) return NotFound("route", routeKey);
                routeId = route.Id;
            }

            var bridgeId = Guid.Empty;
            if (request.Bridge is { Length: > 0 } bridgeKey)
            {
                if (FindBridge(c.Store, bridgeKey) is not { } bridge) return NotFound("bridge", bridgeKey);
                bridgeId = bridge.Id;
            }

            var url = (request.Url ?? "").Trim();

            if (McpFunnelValidation.ValidateTarget(request.Kind, routeId, url, c.Store.Routes, bridgeId, c.Store.McpApiBridges)
                is { } targetError)
            {
                return Bad(targetError);
            }

            var source = new McpSourceRecord
            {
                Name = name,
                Alias = alias,
                Kind = request.Kind,
                RouteId = routeId,
                BridgeId = bridgeId,
                Url = request.Kind == McpSourceKind.RemoteUrl ? url : "",
                Transport = request.Transport,
            };

            await c.Cache.MutateAsync(s => s.McpSources.Add(source));
            c.Changed();

            return Ok(c, $"Source '{name}' added — its tools appear as {alias}{McpNameMapper.Separator}…");
        });

        g.MapDelete("/sources/{key}", async (string key) =>
        {
            if (FindSource(c.Store, key) is not { } source) return NotFound("source", key);

            var affected = 0;
            await c.Cache.MutateAsync(s => affected = ConfigStoreEdits.RemoveSource(s, source.Id));
            await c.Pool.InvalidateSourceAsync(source.Id);
            c.Catalogs.Remove(source.Id);
            c.Changed();

            return Ok(c, affected == 0
                ? $"Source '{source.Name}' deleted."
                : $"Source '{source.Name}' deleted and removed from {affected} funnel(s).");
        });

        // One source, or every enabled one. Concurrently, for the reason the funnel tab gives: the
        // pool is keyed per source, and sequentially one cold upstream holds up the rest.
        g.MapPost("/sources/refresh", async (string? source, CancellationToken ct) =>
        {
            List<McpSourceRecord> due;

            if (source is { Length: > 0 })
            {
                if (FindSource(c.Store, source) is not { } one) return NotFound("source", source);
                due = [one];
            }
            else
            {
                due = [.. c.Store.McpSources.Where(s => s.Enabled)];
            }

            var results = await Task.WhenAll(due.Select(async s =>
            {
                var catalog = await c.Pool.DiscoverAsync(s, ct);
                c.Catalogs.Set(s.Id, catalog);
                return catalog.Error is null ? $"{s.Alias}: {catalog.Describe()}" : $"{s.Alias}: unreachable — {catalog.Detail}";
            }));

            return Results.Ok(new AdminMessage(due.Count == 0
                ? "No enabled sources to refresh."
                : string.Join("\n", results)));
        });

        MapToggle(g, c, "sources", key => FindSource(c.Store, key), s => s.Alias, (s, on) => s.Enabled = on, rebuild: false,
            after: s => c.Pool.InvalidateSourceAsync(s.Id));
    }

    private static string DescribeTarget(ConfigStore store, McpSourceRecord source) => source.Kind switch
    {
        McpSourceKind.ProxyRoute => store.Routes.FirstOrDefault(r => r.Id == source.RouteId)?.PathPrefix ?? "(missing route)",
        McpSourceKind.ApiBridge => store.McpApiBridges.FirstOrDefault(b => b.Id == source.BridgeId) is { } b
            ? $"{McpApiBridgeEndpoints.BasePath}/{b.Slug}"
            : "(missing bridge)",
        _ => source.Url,
    };

    // ---- funnels --------------------------------------------------------------------------------

    private static void MapFunnels(RouteGroupBuilder g, Context c)
    {
        g.MapGet("/funnels", () => Results.Ok(c.Store.McpFunnels.Select(f => new FunnelSummary(
            f.Id, f.Name, f.Slug, f.Enabled,
            [.. f.Sources.Select(link => DescribeMembership(c.Store, link))],
            f.Key.DescribeExpiry(DateTimeOffset.UtcNow)))));

        g.MapPost("/funnels", async (AddFunnelRequest request) =>
        {
            var name = request.Name.Trim();
            if (name.Length == 0) return Bad("Funnel name is required.");

            var slug = string.IsNullOrWhiteSpace(request.Slug) ? ConfigStoreEdits.Slugify(name) : request.Slug.Trim().ToLowerInvariant();
            if (McpFunnelValidation.ValidateSlug(slug, c.Store.McpFunnels) is { } slugError) return Bad(slugError);

            var funnel = new McpFunnelRecord { Name = name, Slug = slug, Key = ProxyKey.Generate(Lifetime(request.KeyLifetimeDays)) };

            await c.Cache.MutateAsync(s => s.McpFunnels.Add(funnel));
            c.Changed();

            return Ok(c, $"Funnel '{name}' added at {McpFunnelEndpoints.BasePath}/{slug} with its own proxy key "
                         + $"({funnel.Key.DescribeExpiry(DateTimeOffset.UtcNow)}) — add sources with "
                         + $"`ravensport funnels source add {slug} <source>`."
                         + (c.Store.Settings.McpFunnelEnabled ? "" : " The MCP funnel is switched off: `ravensport settings set funnel on`."));
        });

        g.MapDelete("/funnels/{key}", async (string key) =>
        {
            if (FindFunnel(c.Store, key) is not { } funnel) return NotFound("funnel", key);

            await c.Cache.MutateAsync(s => s.McpFunnels.Remove(funnel));
            await c.Pool.InvalidateFunnelAsync(funnel.Id);
            c.Changed();

            return Ok(c, $"Funnel '{funnel.Name}' deleted — clients pointed at {McpFunnelEndpoints.BasePath}/{funnel.Slug} will now get 404.");
        });

        g.MapPost("/funnels/{key}/sources", async (string key, FunnelSourceRequest request) =>
        {
            if (FindFunnel(c.Store, key) is not { } funnel) return NotFound("funnel", key);
            if (FindSource(c.Store, request.Source) is not { } source) return NotFound("source", request.Source);

            var tools = request.Tools ?? [];
            if (request.ToolMode != McpSelectionMode.All && tools.Count == 0)
            {
                return Bad($"Tool mode {request.ToolMode} needs at least one tool name.");
            }

            var existed = funnel.Sources.Any(l => l.SourceId == source.Id);

            await c.Cache.MutateAsync(_ =>
            {
                var link = funnel.Sources.FirstOrDefault(l => l.SourceId == source.Id);
                if (link is null)
                {
                    link = new McpFunnelSource { SourceId = source.Id };
                    funnel.Sources.Add(link);
                }

                link.ToolMode = request.ToolMode;
                link.Tools = request.ToolMode == McpSelectionMode.All ? [] : [.. tools];
            });
            c.Changed();

            return Ok(c, existed
                ? $"Updated '{source.Alias}' in funnel '{funnel.Name}': {DescribeSelection(request.ToolMode, tools)}."
                : $"Added '{source.Alias}' to funnel '{funnel.Name}': {DescribeSelection(request.ToolMode, tools)}.");
        });

        g.MapDelete("/funnels/{key}/sources/{source}", async (string key, string source) =>
        {
            if (FindFunnel(c.Store, key) is not { } funnel) return NotFound("funnel", key);
            if (FindSource(c.Store, source) is not { } record) return NotFound("source", source);

            if (!funnel.Sources.Any(l => l.SourceId == record.Id))
            {
                return Bad($"'{record.Alias}' is not in funnel '{funnel.Name}'.");
            }

            await c.Cache.MutateAsync(_ => funnel.Sources.RemoveAll(l => l.SourceId == record.Id));
            c.Changed();
            return Ok(c, $"Removed '{record.Alias}' from funnel '{funnel.Name}'.");
        });

        MapToggle(g, c, "funnels", key => FindFunnel(c.Store, key), f => f.Slug, (f, on) => f.Enabled = on, rebuild: false);
        MapKey(g, c, "funnels", key => FindFunnel(c.Store, key), f => f.Slug, f => f.Key, _ => Task.CompletedTask);
    }

    private static string DescribeMembership(ConfigStore store, McpFunnelSource link)
    {
        var alias = store.McpSources.FirstOrDefault(s => s.Id == link.SourceId)?.Alias ?? "(missing source)";
        return $"{alias} ({DescribeSelection(link.ToolMode, link.Tools)})";
    }

    private static string DescribeSelection(McpSelectionMode mode, IReadOnlyList<string> tools) => mode switch
    {
        McpSelectionMode.Include => $"only {string.Join(", ", tools)}",
        McpSelectionMode.Exclude => $"all except {string.Join(", ", tools)}",
        _ => "all tools",
    };

    // ---- API bridges ----------------------------------------------------------------------------

    private static void MapBridges(RouteGroupBuilder g, Context c)
    {
        g.MapGet("/bridges", () => Results.Ok(c.Store.McpApiBridges.Select(b => new BridgeSummary(
            b.Id, b.Name, b.Slug,
            c.Store.Routes.FirstOrDefault(r => r.Id == b.RouteId)?.PathPrefix ?? "(missing route)",
            b.Enabled, b.Manifest.Tools.Count, b.Key.DescribeExpiry(DateTimeOffset.UtcNow)))));

        g.MapPost("/bridges", async (ImportBridgeRequest request) =>
        {
            if (McpApiBridgeValidation.TryReadManifest(request.ManifestJson, out var manifest) is { } manifestError)
            {
                return Bad(manifestError);
            }

            if (McpApiBridgeValidation.ValidateName(request.Name) is { } nameError) return Bad(nameError);

            var slug = string.IsNullOrWhiteSpace(request.Slug)
                ? ConfigStoreEdits.Slugify(request.Name)
                : request.Slug.Trim().ToLowerInvariant();
            if (McpApiBridgeValidation.ValidateSlug(slug, c.Store.McpApiBridges) is { } slugError) return Bad(slugError);

            if (FindRoute(c.Store, request.Route) is not { } route) return NotFound("route", request.Route);
            if (McpApiBridgeValidation.ValidateRoute(route.Id, c.Store.Routes) is { } routeError) return Bad(routeError);

            var bridge = new McpApiBridgeRecord
            {
                Name = request.Name.Trim(),
                Slug = slug,
                RouteId = route.Id,
                Manifest = manifest!,
                ManifestOrigin = request.Origin,
                Key = ProxyKey.Generate(Lifetime(request.KeyLifetimeDays)),
            };

            // The manifest's canonical home — see ManifestLocalStore. Written, and checked, before
            // the bridge is persisted: a bridge whose tools never reached disk is not worth creating.
            if (ManifestLocalStore.Save(bridge.Id, bridge.Manifest) is { } saveError) return Bad(saveError);

            await c.Cache.MutateAsync(s => s.McpApiBridges.Add(bridge));
            c.Changed();

            return Ok(c, $"API bridge '{bridge.Name}' added at {McpApiBridgeEndpoints.BasePath}/{slug} with "
                         + $"{bridge.Manifest.Tools.Count} tool(s) and its own proxy key ({bridge.Key.DescribeExpiry(DateTimeOffset.UtcNow)})."
                         + (c.Store.Settings.McpApiBridgeEnabled ? "" : " API bridges are switched off: `ravensport settings set bridge on`."));
        });

        g.MapDelete("/bridges/{key}", async (string key) =>
        {
            if (FindBridge(c.Store, key) is not { } bridge) return NotFound("bridge", key);

            IReadOnlyList<Guid> stranded = [];
            await c.Cache.MutateAsync(s => stranded = ConfigStoreEdits.RemoveBridge(s, bridge.Id));
            ManifestLocalStore.Delete(bridge.Id);

            foreach (var sourceId in stranded)
            {
                await c.Pool.InvalidateSourceAsync(sourceId);
                c.Catalogs.Remove(sourceId);
            }

            c.Changed();

            return Ok(c, stranded.Count == 0
                ? $"API bridge '{bridge.Name}' deleted."
                : $"API bridge '{bridge.Name}' deleted, along with {stranded.Count} funnel source(s) that exposed it.");
        });

        MapToggle(g, c, "bridges", key => FindBridge(c.Store, key), b => b.Slug, (b, on) => b.Enabled = on, rebuild: false);
        MapKey(g, c, "bridges", key => FindBridge(c.Store, key), b => b.Slug, b => b.Key, b => InvalidateSourcesForBridgeAsync(c, b.Id));
    }

    private static async Task InvalidateSourcesForBridgeAsync(Context c, Guid bridgeId)
    {
        foreach (var source in c.Store.McpSources.Where(s => s.Kind == McpSourceKind.ApiBridge && s.BridgeId == bridgeId).ToList())
        {
            await c.Pool.InvalidateSourceAsync(source.Id);
        }
    }

    // ---- settings -------------------------------------------------------------------------------

    private static void MapSettings(RouteGroupBuilder g, Context c)
    {
        g.MapGet("/settings", () =>
        {
            var s = c.Store.Settings;
            return Results.Ok(new SettingsReply(
                s.ListenPort, s.McpFunnelEnabled, s.McpApiBridgeEnabled, s.MtlsEnabled,
                HasCertificate: !string.IsNullOrWhiteSpace(s.MtlsClientCertificatePfx) && !string.IsNullOrEmpty(s.MtlsClientCertificatePassword)));
        });

        g.MapPatch("/settings", async (SettingsPatch patch) =>
        {
            var settings = c.Store.Settings;
            var notes = new List<string>();

            if (patch.ListenPort is { } port && port is < 1 or > 65535) return Bad("Listen port must be between 1 and 65535.");

            if (patch.MtlsEnabled is true
                && (string.IsNullOrWhiteSpace(settings.MtlsClientCertificatePfx) || string.IsNullOrEmpty(settings.MtlsClientCertificatePassword)))
            {
                return Bad("Generate a client certificate first, with `ravensport settings mtls generate`. mTLS cannot be enabled until one exists.");
            }

#if STORE_BUILD
            if (patch.MtlsEnabled is true) return Bad("The Microsoft Store build of RavensPort does not support mTLS.");
#endif

            await c.Cache.MutateAsync(s =>
            {
                if (patch.ListenPort is { } p && p != s.Settings.ListenPort)
                {
                    s.Settings.ListenPort = p;
                    notes.Add($"listen port {p} (takes effect when RavensPort restarts)");
                }

                if (patch.McpFunnelEnabled is { } funnel)
                {
                    s.Settings.McpFunnelEnabled = funnel;
                    notes.Add($"MCP funnel {(funnel ? "on" : "off")}");
                }

                if (patch.McpApiBridgeEnabled is { } bridge)
                {
                    s.Settings.McpApiBridgeEnabled = bridge;
                    notes.Add($"API bridges {(bridge ? "on" : "off")}");
                }

                if (patch.MtlsEnabled is { } mtls)
                {
                    s.Settings.MtlsEnabled = mtls;
                    notes.Add($"mTLS {(mtls ? "on" : "off")} (takes effect when RavensPort restarts)");
                }
            });
            c.Changed();

            return Ok(c, notes.Count == 0 ? "Nothing to change." : $"Set {string.Join("; ", notes)}.");
        });

#if !STORE_BUILD
        g.MapPost("/settings/mtls/generate", async (GenerateCertificateRequest request) =>
        {
            if (string.IsNullOrEmpty(request.Password))
            {
                return Bad("A password is required. Clients will need it to load the exported file.");
            }

            var pfx = MtlsCertificateFactory.GenerateClientCertificatePfx(request.Password);
            await c.Cache.MutateAsync(s =>
            {
                s.Settings.MtlsClientCertificatePfx = pfx;
                s.Settings.MtlsClientCertificatePassword = request.Password;
            });
            c.Changed();

            return Results.Ok(new GenerateCertificateReply(
                "Generated a new client certificate. Install the exported file on every client, then restart RavensPort — "
                + "the running listener still presents the previous one." + (c.Gate.IsReadOnly ? $" {ReadOnlyWarning}" : ""),
                pfx));
        });
#endif
    }

    // ---- shared shapes --------------------------------------------------------------------------

    private static void MapToggle<T>(
        RouteGroupBuilder g, Context c, string collection, Func<string, T?> find, Func<T, string> label,
        Action<T, bool> set, bool rebuild, Func<T, Task>? after = null) where T : class
    {
        foreach (var (verb, on) in new[] { ("enable", true), ("disable", false) })
        {
            g.MapPost($"/{collection}/{{key}}/{verb}", async (string key) =>
            {
                if (find(key) is not { } record) return NotFound(collection.TrimEnd('s'), key);

                await c.Cache.MutateAsync(_ => set(record, on));
                if (rebuild) c.RebuildProxy();
                if (after is not null) await after(record);
                c.Changed();

                return Ok(c, $"'{label(record)}' {verb}d.");
            });
        }
    }

    private static void MapKey<T>(
        RouteGroupBuilder g, Context c, string collection, Func<string, T?> find, Func<T, string> label,
        Func<T, ProxyKey> keyOf, Func<T, Task> afterRotate) where T : class
    {
        g.MapGet($"/{collection}/{{key}}/key", (string key) =>
            find(key) is { } record
                ? Results.Ok(new KeyInfo(keyOf(record).Value, keyOf(record).DescribeExpiry(DateTimeOffset.UtcNow)))
                : NotFound(collection.TrimEnd('s'), key));

        g.MapPost($"/{collection}/{{key}}/key/rotate", async (string key) =>
        {
            if (find(key) is not { } record) return NotFound(collection.TrimEnd('s'), key);

            await c.Cache.MutateAsync(_ => keyOf(record).Regenerate());
            await afterRotate(record);
            c.Changed();

            return Ok(c, $"New proxy key for '{label(record)}' ({keyOf(record).DescribeExpiry(DateTimeOffset.UtcNow)}). "
                         + "The old one stops working now; every client using it needs the new one.");
        });
    }

    private static TimeSpan? Lifetime(int? days) => days is > 0 ? TimeSpan.FromDays(days.Value) : null;

    private static IResult Ok(Context c, string message) =>
        Results.Ok(new AdminMessage(message, c.Gate.IsReadOnly ? ReadOnlyWarning : null));

    private static IResult Bad(string message) => Results.BadRequest(new AdminMessage(message));

    private static IResult NotFound(string what, string key) => Results.NotFound(new AdminMessage($"No {what} '{key}'."));

    // ---- lookups --------------------------------------------------------------------------------

    private static bool IsId(string key, Guid id) => Guid.TryParse(key, out var parsed) && parsed == id;

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    internal static CredentialRecord? FindCredential(ConfigStore store, string key) =>
        store.Credentials.FirstOrDefault(x => IsId(key, x.Id) || Same(x.Name, key));

    internal static UpstreamRecord? FindUpstream(ConfigStore store, string key) =>
        store.Upstreams.FirstOrDefault(x => IsId(key, x.Id) || Same(x.Name, key));

    internal static RouteMapping? FindRoute(ConfigStore store, string key)
    {
        var prefix = key.StartsWith('/') ? key : "/" + key;
        return store.Routes.FirstOrDefault(x => IsId(key, x.Id) || Same(x.PathPrefix.TrimEnd('/'), prefix.TrimEnd('/')));
    }

    internal static McpSourceRecord? FindSource(ConfigStore store, string key) =>
        store.McpSources.FirstOrDefault(x => IsId(key, x.Id) || Same(x.Alias, key))
        ?? store.McpSources.FirstOrDefault(x => Same(x.Name, key));

    internal static McpFunnelRecord? FindFunnel(ConfigStore store, string key) =>
        store.McpFunnels.FirstOrDefault(x => IsId(key, x.Id) || Same(x.Slug, key));

    internal static McpApiBridgeRecord? FindBridge(ConfigStore store, string key) =>
        store.McpApiBridges.FirstOrDefault(x => IsId(key, x.Id) || Same(x.Slug, key));
}
