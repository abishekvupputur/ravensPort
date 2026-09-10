using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Proxy;
using RavensPort.Core.Storage;

namespace RavensPort.Core.Mcp;

/// <summary>
/// The path space the API bridges own, and the one rule about it that several layers need to
/// agree on: which slug a request names.
///
/// Deliberately a mirror of <see cref="McpFunnelEndpoints"/> rather than a generalisation of it.
/// The two endpoints answer different questions — a funnel pools MCP servers, a bridge turns one
/// REST API into one — and a shared base class would have to be parameterised by nearly every
/// line that differs between them.
/// </summary>
public static class McpApiBridgeEndpoints
{
    /// <summary>
    /// Path segment the bridges own. Routes are forbidden from claiming it (see
    /// <see cref="Models.RouteValidation.ReservedPathPrefixes"/>).
    /// </summary>
    public const string BasePath = "/api-mcp";

    /// <summary>Route parameter the bridge slug is matched into.</summary>
    public const string SlugRouteValue = "bridge";

    /// <summary>
    /// Refuses bridge traffic that should never have got this far, before the MCP machinery sees
    /// it. Sits after <see cref="LocalAccessGuard"/>, so the caller is already known to hold this
    /// endpoint's key — this is about the bridge's own preconditions:
    ///
    ///   • the feature is switched off, so /api-mcp should look like it does not exist;
    ///   • the slug names no enabled bridge, likewise;
    ///   • the request carries a bridge's own hop marker, meaning a bridge's tool call reached a
    ///     route that led back here. Left alone it recurses until something breaks.
    ///
    /// A funnel's hop marker is deliberately <em>not</em> refused: a funnel pooling a bridge is
    /// the feature, and it is the funnel gate — not this one — that turns the marker away.
    ///
    /// Unknown and disabled both answer 404 rather than 403, for the same reason the funnel does:
    /// a caller holding a valid key still should not be able to enumerate which bridges exist by
    /// watching status codes.
    /// </summary>
    public static IApplicationBuilder UseMcpApiBridgeGate(this IApplicationBuilder app)
    {
        var configStoreCache = app.ApplicationServices.GetService(typeof(ConfigStoreCache)) as ConfigStoreCache
                               ?? throw new InvalidOperationException("ConfigStoreCache is not registered.");
        var handlerFactory = app.ApplicationServices.GetService(typeof(McpApiBridgeHandlerFactory)) as McpApiBridgeHandlerFactory
                             ?? throw new InvalidOperationException("McpApiBridgeHandlerFactory is not registered.");
        var activityLog = app.ApplicationServices.GetService(typeof(ActivityLog)) as ActivityLog
                          ?? throw new InvalidOperationException("ActivityLog is not registered.");

        return app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(BasePath))
            {
                await next();
                return;
            }

            if (!configStoreCache.Current.Settings.McpApiBridgeEnabled)
            {
                await NotFound(context);
                return;
            }

            if (context.Items.TryGetValue(LocalAccessGuard.BridgeHopItemKey, out var hop) && hop is true)
            {
                activityLog.Log($"MCP bridge refused a request that had already passed through a bridge — {context.Request.Path} would loop");
                await NotFound(context);
                return;
            }

            if (handlerFactory.FindBridge(ExtractSlug(context.Request.Path)) is null)
            {
                await NotFound(context);
                return;
            }

            await next();
        });
    }

    /// <summary>
    /// Maps every bridge at one pattern. Which bridge a request belongs to is decided per request
    /// from the slug (see ConfigureSessionOptions in ProxyStartupExtensions), so importing a
    /// manifest needs no remapping and no restart.
    /// </summary>
    public static IEndpointConventionBuilder MapMcpApiBridge(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapMcp($"{BasePath}/{{{SlugRouteValue}}}");

    /// <summary>
    /// First segment after /api-mcp. The SDK also serves sub-paths beneath the pattern (the
    /// legacy SSE endpoints), so anything past the slug is ignored here.
    /// </summary>
    public static string? ExtractSlug(PathString path)
    {
        if (!path.StartsWithSegments(BasePath, out var remainder)) return null;

        var value = remainder.Value?.Trim('/');
        if (string.IsNullOrEmpty(value)) return null;

        var slash = value.IndexOf('/');
        return slash < 0 ? value : value[..slash];
    }

    private static async Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync("No such MCP API bridge endpoint.", context.RequestAborted);
    }
}
