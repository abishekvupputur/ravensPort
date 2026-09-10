using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Mcp;

public static class McpFunnelEndpoints
{
    /// <summary>
    /// Path segment the funnel owns. Routes are forbidden from claiming it
    /// (see <see cref="Models.RouteValidation.ReservedPathPrefixes"/>).
    /// </summary>
    public const string BasePath = "/mcp";

    /// <summary>Route parameter <see cref="MapMcpFunnel"/> matches the slug into.</summary>
    public const string SlugRouteValue = "funnel";

    /// <summary>
    /// Refuses funnel traffic that should never have got this far, before the MCP machinery sees
    /// it. Sits after <see cref="LocalAccessGuard"/>, so the caller is already known to hold the
    /// local API key — this is about the funnel's own preconditions:
    ///
    ///   • the feature is switched off, so /mcp should look like it does not exist;
    ///   • the slug names no enabled funnel, likewise;
    ///   • the request carries either hop marker. The funnel's own means a funnel reached one of
    ///     its sources and that source led back here; a bridge's means a bridge's tool call
    ///     reached a route that led here. Left alone, both recurse until something breaks. The
    ///     bridge gate refuses only its own marker, because a funnel calling a bridge is the
    ///     feature — this is the direction that never is.
    ///
    /// Unknown and disabled both answer 404 rather than 403: a caller with a valid key still
    /// should not be able to enumerate which funnels exist by watching status codes.
    /// </summary>
    public static IApplicationBuilder UseMcpFunnelGate(this IApplicationBuilder app)
    {
        var configStoreCache = app.ApplicationServices.GetService(typeof(ConfigStoreCache)) as ConfigStoreCache
                               ?? throw new InvalidOperationException("ConfigStoreCache is not registered.");
        var handlerFactory = app.ApplicationServices.GetService(typeof(McpFunnelHandlerFactory)) as McpFunnelHandlerFactory
                             ?? throw new InvalidOperationException("McpFunnelHandlerFactory is not registered.");
        var activityLog = app.ApplicationServices.GetService(typeof(ActivityLog)) as ActivityLog
                          ?? throw new InvalidOperationException("ActivityLog is not registered.");

        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(BasePath))
            {
                await next();
                return;
            }

            if (!configStoreCache.Current.Settings.McpFunnelEnabled)
            {
                await NotFound(context);
                return;
            }

            if (HasHopMarker(context, LocalAccessGuard.FunnelHopItemKey)
                || HasHopMarker(context, LocalAccessGuard.BridgeHopItemKey))
            {
                activityLog.Log($"MCP funnel refused a request that had already passed through a funnel or an API bridge — {context.Request.Path} would loop");
                await NotFound(context);
                return;
            }

            if (handlerFactory.FindFunnel(ExtractSlug(context.Request.Path)) is null)
            {
                await NotFound(context);
                return;
            }

            await next();
        });
    }

    /// <summary>
    /// Maps every funnel at one pattern. Which funnel a request belongs to is decided per request
    /// from the slug (see the ConfigureSessionOptions callback in ProxyStartupExtensions), so
    /// creating or deleting a funnel in the GUI needs no remapping and no restart.
    /// </summary>
    public static IEndpointConventionBuilder MapMcpFunnel(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp($"{BasePath}/{{{SlugRouteValue}}}");

    /// <summary>
    /// First segment after /mcp. The SDK also serves sub-paths beneath the pattern (the legacy
    /// SSE endpoints), so anything past the slug is ignored here.
    /// </summary>
    public static string? ExtractSlug(PathString path)
    {
        if (!path.StartsWithSegments(BasePath, out var remainder)) return null;

        var value = remainder.Value?.Trim('/');
        if (string.IsNullOrEmpty(value)) return null;

        var slash = value.IndexOf('/');
        return slash < 0 ? value : value[..slash];
    }

    private static bool HasHopMarker(HttpContext context, string itemKey) =>
        context.Items.TryGetValue(itemKey, out var hop) && hop is true;

    private static async Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("No such MCP funnel endpoint.", context.RequestAborted);
    }
}
