using RavensPort.Core.Auth;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;
using RavensPort.Core.Storage;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace RavensPort.Core.Proxy;

/// <summary>
/// Attaches the route's credentials to each proxied request, in whichever shapes the route is
/// configured for — headers (the "Authorization: Bearer &lt;token&gt;" default), fields in the
/// request body, or both. Query-string placements are refused rather than sent; see
/// <see cref="CredentialPlacement.Query"/>.
///
/// A route may carry none, one, or several credentials, and the same credential may appear in
/// more than one slot. Body fields are collected and written in a single pass at the end, so a
/// request whose body carries two credentials is buffered and re-serialized once rather than
/// twice.
///
/// Tokens are read from ConfigStoreCache live on every request (not captured at route-build
/// time). This is what decouples token refresh from route rebuilds — a refreshed token applies
/// to the very next proxied request automatically, no config reload needed.
/// </summary>
public sealed class CredentialInjectionTransformProvider(
    ConfigStoreCache configStoreCache,
    AccessTokenProvider accessTokenProvider,
    ActivityLog activityLog) : ITransformProvider
{
    public void ValidateRoute(TransformRouteValidationContext context)
    {
    }

    public void ValidateCluster(TransformClusterValidationContext context)
    {
    }

    public void Apply(TransformBuilderContext context)
    {
        // Presence of the key, not of any credential in it: a route configured to attach nothing
        // still needs the transform below, because clearing the caller's own Authorization and
        // cookies is not part of attaching a credential — it is what stops a local caller using
        // this proxy as a courier for credentials it should not be able to reach.
        if (context.Route.Metadata is not { } metadata ||
            !metadata.ContainsKey(ProxyConfigBuilder.CredentialsMetadataKey))
        {
            return;
        }

        var credentials = ProxyConfigBuilder.ReadCredentials(metadata);

        context.AddRequestTransform(async transformContext =>
        {
            // Cleared unconditionally — whatever the route's placements are, and whether or not
            // there is a token to attach. YARP copies request headers through by default, so
            // leaving these alone would forward the caller's own Authorization header and cookies
            // to the upstream, letting a local caller spend credentials (or an ambient browser
            // session) it should not have been able to reach.
            transformContext.ProxyRequest.Headers.Authorization = null;
            transformContext.ProxyRequest.Headers.Remove("Cookie");

            var attached = new List<string>();
            var failed = new List<string>();

            // Body placements are gathered rather than applied in the loop; see WriteBodyAsync.
            var bodyFields = new List<KeyValuePair<string, string>>();
            var bodyLabels = new List<string>();

            foreach (var routeCredential in credentials)
            {
                var injection = routeCredential.ToCredentialInjection();
                var credential = configStoreCache.GetCredential(routeCredential.CredentialId);
                var label = $"{credential?.Name ?? "(deleted credential)"} via {Describe(injection)}";

                // Not credential.Token.AccessToken directly: this refreshes first if the token has
                // already expired, so a request arriving between refresh-loop ticks (or after the
                // machine slept through one) still goes out authenticated instead of 401-ing.
                var token = await accessTokenProvider.GetAccessTokenAsync(
                    routeCredential.CredentialId, transformContext.HttpContext.RequestAborted);

                if (token is null)
                {
                    failed.Add($"{label} (credential not connected)");
                    continue;
                }

                // A store written before query placements were withdrawn can still name one.
                // Refused rather than downgraded to a header: the upstream expects the parameter
                // and would reject a header it does not read, so quietly "fixing" it would turn a
                // clear failure into a confusing one. Checked before the token is fetched so a
                // route nobody can use does not keep refreshing a grant on every request.
                if (!injection.IsPermitted)
                {
                    failed.Add($"{label} (query-string placements are no longer permitted — "
                               + "change it to a header or body field on the Routes tab)");
                    continue;
                }

                if (injection.Placement == CredentialPlacement.Body)
                {
                    bodyFields.Add(new KeyValuePair<string, string>(injection.Name, injection.FormatValue(token)));
                    bodyLabels.Add(label);
                    continue;
                }

                if (Inject(transformContext, injection, token))
                {
                    attached.Add(label);
                }
                else
                {
                    failed.Add($"{label} (could not be attached)");
                }
            }

            if (bodyFields.Count > 0)
            {
                var wrote = await RequestBodyCredentialInjector.TryInjectAsync(
                    transformContext, bodyFields, activityLog, string.Join(", ", bodyLabels));

                (wrote ? attached : failed).AddRange(bodyLabels);
            }

            Log(transformContext, credentials.Count, attached, failed);
        });

        context.AddResponseTransform(transformContext =>
        {
            var status = transformContext.ProxyResponse?.StatusCode;
            var request = transformContext.HttpContext.Request;

            // The request body has already been streamed upstream by this point, so replaying
            // it here is not possible. Flagging the credentials is the next best thing: the
            // periodic loop picks them up and the user sees "Needs reconnect" in the UI, instead
            // of silent 401s with no indication of which credential went bad.
            //
            // Every credential on the route is flagged because a 401 does not say which of them
            // the upstream objected to.
            if (status == System.Net.HttpStatusCode.Unauthorized)
            {
                foreach (var routeCredential in credentials)
                {
                    if (configStoreCache.GetCredential(routeCredential.CredentialId) is not { } credential) continue;

                    activityLog.Log(
                        $"AUTH '{credential.Name}' rejected by upstream (401) — token refresh will be retried, "
                        + "reconnect if this repeats");
                    credential.NeedsReconnect = credential.Token?.RefreshToken is null;
                }
            }

            activityLog.Log(
                $"  <- {(status is null ? "no response (upstream unreachable)" : ((int)status).ToString())}"
                + $"{DescribeContentType(transformContext.ProxyResponse)} for {request.Method} {LogSafePath(request)}");
            return ValueTask.CompletedTask;
        });
    }

    private static string Describe(CredentialInjection injection) =>
        $"{injection.Placement.ToString().ToLowerInvariant()} {injection.Name}";

    /// <summary>
    /// One activity-log line per proxied request, naming every credential that made it onto the
    /// request and every one that did not. A route that attaches nothing says so explicitly —
    /// otherwise an intentional pass-through route and a broken credential would produce
    /// indistinguishable log lines.
    /// </summary>
    private void Log(
        RequestTransformContext context, int configured, IReadOnlyList<string> attached, IReadOnlyList<string> failed)
    {
        var request = context.HttpContext.Request;
        var head = $"PROXY {request.Method} {LogSafePath(request)} -> {context.DestinationPrefix}";

        if (configured == 0)
        {
            activityLog.Log($"{head} [no credential configured - forwarded unauthenticated]");
            return;
        }

        var parts = new List<string>();
        if (attached.Count > 0) parts.Add($"tokens: {string.Join("; ", attached)}");
        if (failed.Count > 0) parts.Add($"NOT ATTACHED: {string.Join("; ", failed)}");

        activityLog.Log($"{head} [{string.Join(" | ", parts)}]");
    }

    /// <summary>
    /// The response's media type, for any status that is not a plain success.
    ///
    /// Media type only — never the body, which carries user data. This one word separates two
    /// failures that otherwise look identical in the log: an upstream that answered 200 with the
    /// JSON the client wanted, and one that answered 200 with an HTML sign-in or landing page.
    /// The second reads as success everywhere except inside the client, which then reports
    /// something oblique like "response completed without a reply".
    /// </summary>
    private static string DescribeContentType(HttpResponseMessage? response)
    {
        if (response?.Content.Headers.ContentType?.MediaType is not { } mediaType) return "";

        // A JSON or event-stream answer is the expected case and needs no annotation.
        return mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)
               || mediaType.Contains("event-stream", StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" [{mediaType}]";
    }

    /// <summary>
    /// Puts one token where its entry says it goes. Headers only — body placements are batched by
    /// the caller so the body is rewritten once regardless of how many credentials live in it,
    /// and query placements never reach here.
    /// </summary>
    private static bool Inject(RequestTransformContext context, CredentialInjection injection, string token)
    {
        var value = injection.FormatValue(token);

        // The route's value prefix is validated when it is entered, but the secret itself is not
        // always: an API key is whatever the user pasted, and a CR or LF picked up from a wrapped
        // email would end the header line and let the rest be read as further headers — request
        // splitting, aimed at the upstream. TryAddWithoutValidation is exactly as permissive as
        // its name says, so the check has to happen here. Refused rather than sanitized: a
        // silently trimmed key is a key that does not work, reported as one that does.
        if (value.Any(char.IsControl)) return false;

        // Belt and braces. The caller filters these out already; reaching here would mean a token
        // going into a query string, which is the thing this placement was withdrawn to prevent.
        if (!injection.IsPermitted) return false;

        // Removed first because TryAddWithoutValidation appends rather than replaces, so without
        // this a caller-supplied header of the same name would be sent alongside ours and the
        // upstream would pick whichever it liked.
        //
        // Both collections are tried because HttpClient splits headers between them and
        // which one a given name lives in depends on the name, not on the caller.
        TryRemove(context.ProxyRequest.Headers, injection.Name);
        if (context.ProxyRequest.Content is { } content)
        {
            TryRemove(content.Headers, injection.Name);
        }

        return context.ProxyRequest.Headers.TryAddWithoutValidation(injection.Name, value);
    }

    /// <summary>
    /// Removes a header if that collection is allowed to hold it.
    ///
    /// HttpHeaders.Remove throws "Misused header name" when asked for a name belonging to the
    /// other collection — request headers on an HttpContent, or content headers on an
    /// HttpRequestMessage. That threw straight out of the transform and became a bare 502, which
    /// is how every POST through a header-placement route used to fail: a GET has no Content at
    /// all, so the faulty call was never reached, and only requests with a body hit it. MCP
    /// traffic is entirely POST, so the funnel met it immediately.
    ///
    /// The exception is the answer, not an error: a name this collection cannot hold is a name it
    /// cannot have a stale value under either.
    /// </summary>
    private static void TryRemove(System.Net.Http.Headers.HttpHeaders headers, string name)
    {
        try
        {
            headers.Remove(name);
        }
        catch (InvalidOperationException)
        {
            // Not a header this collection can carry; nothing to clear.
        }
    }

    /// <summary>
    /// Path plus query *keys only*. Activity logs are plaintext on disk beside the encrypted
    /// store and are kept for days, while query strings routinely carry API keys, document
    /// ids, search terms, and email addresses — logging them raw quietly undid much of what
    /// encrypting the store bought. The local API key can also arrive as a query parameter,
    /// which must never be written down.
    /// </summary>
    private static string LogSafePath(Microsoft.AspNetCore.Http.HttpRequest request)
    {
        if (!request.QueryString.HasValue || request.Query.Count == 0)
        {
            return request.Path;
        }

        var keys = string.Join("&", request.Query.Keys.Select(k => $"{k}=<redacted>"));
        return $"{request.Path}?{keys}";
    }
}
