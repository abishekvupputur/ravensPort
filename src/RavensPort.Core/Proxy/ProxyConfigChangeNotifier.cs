using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using Yarp.ReverseProxy.Configuration;

namespace RavensPort.Core.Proxy;

/// <summary>
/// Rebuilds and hot-applies YARP's route/cluster config from the current ConfigStoreCache
/// state. Call after any edit to Routes/Upstreams (or on initial load).
/// </summary>
public sealed class ProxyConfigChangeNotifier(
    ConfigStoreCache configStoreCache,
    InMemoryConfigProvider configProvider,
    ActivityLog activityLog)
{
    public void Rebuild()
    {
        var store = configStoreCache.Current;
        var (routes, clusters) = ProxyConfigBuilder.Build(store.Routes, store.Upstreams);
        configProvider.Update(routes, clusters);
        activityLog.Log($"ROUTES reloaded — {routes.Count} active route(s)");
        foreach (var route in routes)
        {
            activityLog.Log($"  {route.Match.Path} -> {clusters.First(c => c.ClusterId == route.ClusterId).Destinations!["d1"].Address}");
        }

        // The log used to report only what was built, so a route dropped by the builder looked
        // identical to one that was never configured — the user saw a healthy "reloaded" line
        // and a route that silently did nothing. Name each one and why it went.
        foreach (var mapping in store.Routes.Where(r => r.Enabled))
        {
            if (RouteValidation.ValidatePathPrefix(mapping.PathPrefix) is { } prefixError)
            {
                activityLog.Log($"  SKIPPED '{mapping.PathPrefix}' — {prefixError}");
            }
            else if (store.Upstreams.All(u => u.Id != mapping.UpstreamId))
            {
                activityLog.Log($"  SKIPPED '{mapping.PathPrefix}' — its upstream no longer exists");
            }
            else if (RouteValidation.ValidateCredentials(mapping.Credentials) is { } credentialError)
            {
                // The third way a route disappears, and the one a user is most likely to meet
                // without having touched anything: a route stored with a query-string placement
                // stopped being buildable when that placement was withdrawn. Unnamed, it would be
                // a 404 on a route the tab still lists as enabled.
                activityLog.Log($"  SKIPPED '{mapping.PathPrefix}' — {credentialError}");
            }
        }
    }
}
