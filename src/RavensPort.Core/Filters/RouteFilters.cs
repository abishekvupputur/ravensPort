using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using Yarp.ReverseProxy.Model;

namespace RavensPort.Core.Filters;

/// <summary>
/// Filters for plain proxy routes: a route with a filter profile has each request's query values
/// and body checked by that profile's steps before YARP forwards it, exactly as a funnel checks a
/// tool call's arguments. A Block answers 403 with the reason, and the upstream never sees the
/// request.
///
/// A body is read when it is JSON (its fields become the texts the steps read), a form, or text;
/// up to 1 MB, the same ceiling the credential injector buffers to. A larger body, or one that
/// cannot be read, is refused rather than forwarded unchecked — a route that carries uploads
/// should be left without a profile.
/// </summary>
public static class RouteFilters
{
    private const long MaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// The app's reverse proxy: YARP's own pipeline with route filtering in front. Used by the app
    /// and by every test host, so they run the same pipeline.
    /// </summary>
    public static ReverseProxyConventionBuilder MapRavensPortProxy(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapReverseProxy(pipeline =>
        {
            pipeline.Use(FilterAsync);

            // What MapReverseProxy() runs when given no pipeline of its own.
            pipeline.UseSessionAffinity();
            pipeline.UseLoadBalancing();
            pipeline.UsePassiveHealthChecks();
        });

    private static async Task FilterAsync(HttpContext context, Func<Task> next)
    {
        var services = context.RequestServices;
        var store = services.GetRequiredService<ConfigStoreCache>().Current;
        var routeId = context.GetReverseProxyFeature().Route.Config.RouteId;
        var route = store.Routes.FirstOrDefault(r => string.Equals(r.Id.ToString(), routeId, StringComparison.OrdinalIgnoreCase));

        if (route?.FilterProfileId is null)
        {
            await next();
            return;
        }

        var log = services.GetRequiredService<ActivityLog>();
        var what = $"ROUTE {route.PathPrefix} {context.Request.Method}";

        FilterChoice choice;
        try
        {
            choice = FilterProfiles.For(store, route.FilterProfileId, "route");
        }
        catch (FilterProfileMissingException ex)
        {
            await RefuseAsync(context, log, what, ex.Message);
            return;
        }

        if (choice.Filters.Count == 0)
        {
            await next();
            return;
        }

        List<KeyValuePair<string, JsonElement>> arguments;
        try
        {
            arguments = await ReadAsync(context.Request, context.RequestAborted);
        }
        catch (InvalidDataException ex)
        {
            await RefuseAsync(context, log, what, ex.Message);
            return;
        }

        var hits = await services.GetRequiredService<FilterService>()
            .EvaluateAsync(choice.Filters, $"{context.Request.Method} {context.Request.Path}", arguments, context.RequestAborted);

        foreach (var hit in hits)
        {
            log.Log($"{what} -> {(hit.Action == FilterAction.Block ? "blocked" : "watched")} by {choice.Source}: {hit.Describe()}");
        }

        if (hits.FirstOrDefault(h => h.Action == FilterAction.Block) is { } blocked)
        {
            await RefuseAsync(context, log: null, what, $"Blocked by a RavensPort filter ({blocked.Describe()}). The request was not sent.");
            return;
        }

        await next();
    }

    private static async Task RefuseAsync(HttpContext context, ActivityLog? log, string what, string reason)
    {
        log?.Log($"{what} -> blocked: {reason}");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "blocked_by_filter", reason });
    }

    /// <summary>The request as the texts a filter reads: query values under "query.name", then the body.</summary>
    internal static async Task<List<KeyValuePair<string, JsonElement>>> ReadAsync(HttpRequest request, CancellationToken ct)
    {
        var arguments = new List<KeyValuePair<string, JsonElement>>();

        foreach (var (name, values) in request.Query)
        {
            arguments.Add(KeyValuePair.Create($"query.{name}", JsonSerializer.SerializeToElement(values.ToString())));
        }

        if (request.ContentLength is 0 || (request.ContentLength is null && !request.Headers.TransferEncoding.Any()))
        {
            return arguments;
        }

        if (request.ContentLength > MaxBodyBytes)
        {
            throw new InvalidDataException($"The request body is larger than {MaxBodyBytes / 1024 / 1024} MB, too large to check; this route's filter profile refuses what it cannot read.");
        }

        request.EnableBuffering();
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer, ct);
        request.Body.Position = 0;

        if (buffer.Length > MaxBodyBytes)
        {
            throw new InvalidDataException($"The request body is larger than {MaxBodyBytes / 1024 / 1024} MB, too large to check; this route's filter profile refuses what it cannot read.");
        }

        var text = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        var type = request.ContentType ?? "";

        if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(text);
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    arguments.AddRange(json.RootElement.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, p.Value.Clone())));
                }
                else
                {
                    arguments.Add(KeyValuePair.Create("body", json.RootElement.Clone()));
                }
                return arguments;
            }
            catch (JsonException)
            {
                // Said JSON, is not: read it as text below, so nothing in it goes unchecked.
            }
        }

        if (type.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var pair in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
                var value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                arguments.Add(KeyValuePair.Create(name, JsonSerializer.SerializeToElement(value)));
            }
            return arguments;
        }

        arguments.Add(KeyValuePair.Create("body", JsonSerializer.SerializeToElement(text)));
        return arguments;
    }
}
