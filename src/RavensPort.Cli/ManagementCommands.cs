using System.CommandLine;
using RavensPort.Core.Admin;
using RavensPort.Core.Models;

namespace RavensPort.Cli;

/// <summary>
/// Every command that manages a running RavensPort. Each is a thin translation from arguments to
/// one admin request: validation, defaults and cascades all happen on the server, in the same code
/// the desktop tabs use, so a rule cannot be enforced here and forgotten there.
/// </summary>
internal static class ManagementCommands
{
    // ---- status and reload ----------------------------------------------------------------------

    public static Command Status(CommandContext c) => Leaf("status", "Show what the running RavensPort is serving.", c,
        async (_, client, _) =>
        {
            var (status, code) = await client.GetAsync<AdminStatus>("status");
            if (status is null || client.Json) return code;

            var o = client.Out;
            o.WriteLine($"Host:     {status.Host} (version {status.Version})");
            o.WriteLine($"Vault:    {status.Backend} — {status.VaultName}{(status.ReadOnly ? " (READ-ONLY)" : "")}");
            o.WriteLine($"Proxy:    {status.Scheme}://127.0.0.1:{status.ListenPort}");
            o.WriteLine($"Features: MCP funnel {OnOff(status.McpFunnelEnabled)}, API bridges {OnOff(status.McpApiBridgeEnabled)}, mTLS {OnOff(status.MtlsEnabled)}");
            o.WriteLine($"Store:    {status.Credentials} credential(s), {status.Upstreams} upstream(s), {status.Routes} route(s), "
                        + $"{status.Sources} source(s), {status.Funnels} funnel(s), {status.Bridges} bridge(s)");
            o.WriteLine(status.ReadOnly
                ? $"Changes:  {(status.PendingChanges ? "held in memory — discarded when RavensPort exits" : "none")}"
                : $"Changes:  {(status.PendingChanges ? $"not yet saved to the vault ({status.SyncState})" : "all saved")}");
            return code;
        });

    public static Command Reload(CommandContext c)
    {
        var force = new Option<bool>("--force") { Description = "Reload even if that discards changes not yet in the vault." };

        return Leaf("reload", "Re-read the configuration from the vault — for edits made elsewhere.", c,
            (parse, client, _) => client.PostAsync("reload", new ReloadRequest(parse.GetValue(force))), force);
    }

    // ---- credentials ----------------------------------------------------------------------------

    public static Command Credentials(CommandContext c)
    {
        var name = new Argument<string>("name") { Description = "Credential name or id." };

        return Group("credentials", "Credentials: OAuth2 logins, API keys, app logins and service accounts.",
            Leaf("list", "List credentials and whether each holds a token.", c, async (_, client, _) =>
            {
                var (rows, code) = await client.GetAsync<List<CredentialSummary>>("credentials");
                if (rows is not null && !client.Json)
                {
                    Table.Write(client.Out, rows, "No credentials.",
                        ("NAME", r => r.Name), ("KIND", r => CredentialKindInfo.ShortLabel(r.Kind)),
                        ("TOKEN", r => r.NeedsReconnect ? "needs sign-in" : r.TokenExpiry));
                }

                return code;
            }),
            CredentialAdd(c),
            Leaf("remove", "Delete a credential.", c,
                (parse, client, _) => client.DeleteAsync($"credentials/{Escape(parse.GetValue(name)!)}"), name),
            Leaf("test", "Call the credential's test endpoint with its current token.", c, async (parse, client, _) =>
            {
                var (reply, code) = await client.SendForAsync<CredentialTestReply>(
                    HttpMethod.Post, $"credentials/{Escape(parse.GetValue(name)!)}/test", null);
                if (reply is null) return code;
                if (!client.Json) client.Out.WriteLine(reply.Message);
                return reply.Success ? ExitCodes.Ok : ExitCodes.Refused;
            }, name),
            Leaf("signin", "Connect a credential: shows the device code or authorization URL, and waits.", c,
                (parse, client, ct) => client.StreamAsync($"credentials/{Escape(parse.GetValue(name)!)}/signin", ct), name),
            Leaf("signout", "Clear a credential's stored token (it is not revoked at the provider).", c,
                (parse, client, _) => client.PostAsync($"credentials/{Escape(parse.GetValue(name)!)}/signout"), name));
    }

    private static Command CredentialAdd(CommandContext c)
    {
        var name = new Argument<string>("name") { Description = "A name for the credential, unique among them." };
        var kind = new Option<CredentialKind>("--kind")
        {
            Description = "oauth2, device-code, api-key, client-credentials, service-account or token-exchange.",
            Required = true,
            CustomParser = result => ParseKind(result),
        };
        var preset = new Option<string?>("--preset") { Description = "Fill in a provider's endpoints and scopes: google, github or nextcloud." };
        var clientId = new Option<string?>("--client-id") { Description = "OAuth client id." };
        var scopes = new Option<string?>("--scopes") { Description = "Scopes, separated by commas or spaces." };
        var authority = new Option<string?>("--authority") { Description = "OIDC authority, for discovery." };
        var authorizationEndpoint = new Option<string?>("--authorization-endpoint") { Description = "OAuth authorization endpoint." };
        var tokenEndpoint = new Option<string?>("--token-endpoint") { Description = "OAuth token endpoint." };
        var deviceEndpoint = new Option<string?>("--device-endpoint") { Description = "Device authorization endpoint (device-code)." };
        var noPkce = new Option<bool>("--no-pkce") { Description = "Do not use PKCE (Google browser flow only)." };
        var extraParams = new Option<string?>("--extra-params") { Description = "Extra authorization parameters, as a query string." };
        var secretInBody = new Option<bool>("--secret-in-body") { Description = "Send the client secret in the token request body rather than Basic auth." };
        var withSecret = new Option<bool>("--with-client-secret")
        {
            Description = "Read a client secret from stdin (always read for client-credentials).",
        };
        var serviceAccountFile = new Option<FileInfo?>("--service-account-file") { Description = "Google service account key file (JSON)." };
        var subject = new Option<string?>("--subject") { Description = "Service account: the user to impersonate (domain-wide delegation)." };
        var exchangeEndpoint = new Option<string?>("--exchange-endpoint") { Description = "Token exchange: the endpoint to call." };
        var exchangeMode = new Option<TokenExchangeMode>("--exchange-mode") { Description = "Token exchange: ApiKey or CustomBody.", DefaultValueFactory = _ => TokenExchangeMode.ApiKey };
        var exchangeHeader = new Option<string?>("--exchange-header") { Description = "Token exchange: header carrying the API key." };
        var exchangePrefix = new Option<string?>("--exchange-prefix") { Description = "Token exchange: prefix before the API key." };
        var requestBodyFile = new Option<FileInfo?>("--request-body-file") { Description = "Token exchange (CustomBody): JSON request body file." };
        var tokenPath = new Option<string?>("--token-path") { Description = "Token exchange: JSON path of the token in the reply." };
        var expiresPath = new Option<string?>("--expires-in-path") { Description = "Token exchange: JSON path of the lifetime in the reply." };
        var placement = new Option<CredentialPlacement?>("--placement") { Description = "Where routes send it by default: Header, Query or Body." };
        var parameter = new Option<string?>("--parameter") { Description = "Header, query or body field name." };
        var valuePrefix = new Option<string?>("--value-prefix") { Description = "Text before the value, e.g. \"Bearer \"." };
        var testEndpoint = new Option<string?>("--test-endpoint") { Description = "A URL to call when testing the credential." };

        // Secrets on the command line are refused by name, rather than left to "unrecognized
        // option": the person reaching for one should learn where secrets go instead.
        var apiKeyOnArgv = new Option<string?>("--api-key") { Hidden = true };
        var clientSecretOnArgv = new Option<string?>("--client-secret") { Hidden = true };

        var command = new Command("add",
            "Add a credential. Secrets — an API key, a client secret, a token-exchange key — are read from "
            + "stdin, or asked for without echo; never from arguments.")
        {
            name, kind, preset, clientId, scopes, authority, authorizationEndpoint, tokenEndpoint, deviceEndpoint, noPkce,
            extraParams, secretInBody, withSecret, serviceAccountFile, subject, exchangeEndpoint, exchangeMode, exchangeHeader,
            exchangePrefix, requestBodyFile, tokenPath, expiresPath, placement, parameter, valuePrefix, testEndpoint,
            apiKeyOnArgv, clientSecretOnArgv,
        };

        command.Validators.Add(result =>
        {
            if (result.GetResult(apiKeyOnArgv) is not null || result.GetResult(clientSecretOnArgv) is not null)
            {
                result.AddError("Secrets are not accepted as arguments — a command line is visible to every process on "
                                + "this machine and lands in shell history. Leave it out and RavensPort reads it from stdin.");
            }
        });

        command.SetAction(async (parse, ct) =>
        {
            using var client = c.Client(parse);
            var credentialKind = parse.GetValue(kind);

            var record = new CredentialRecord { Name = parse.GetValue(name)!.Trim(), Kind = credentialKind };

            if (parse.GetValue(preset) is { } presetName)
            {
                if (OAuthProviderPreset.All.FirstOrDefault(p => p.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase)) is not { } p)
                {
                    c.Error.WriteLine($"error: unknown preset '{presetName}'. Use google, github or nextcloud.");
                    return ExitCodes.Usage;
                }

                ApplyPreset(record, p);
            }

            record.ClientId = parse.GetValue(clientId)?.Trim() ?? record.ClientId;
            if (parse.GetValue(scopes) is { } scopeText)
            {
                record.Scopes = [.. scopeText.Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
            }

            record.Authority = Blank(parse.GetValue(authority)) ?? record.Authority;
            record.AuthorizationEndpoint = Blank(parse.GetValue(authorizationEndpoint)) ?? record.AuthorizationEndpoint;
            record.TokenEndpoint = Blank(parse.GetValue(tokenEndpoint)) ?? record.TokenEndpoint;
            record.DeviceAuthorizationEndpoint = Blank(parse.GetValue(deviceEndpoint)) ?? record.DeviceAuthorizationEndpoint;
            if (parse.GetValue(noPkce)) record.UsesPkce = false;
            record.ExtraAuthParams = Blank(parse.GetValue(extraParams));
            record.SendClientCredentialsInBody = parse.GetValue(secretInBody);
            record.ServiceAccountSubject = Blank(parse.GetValue(subject));
            record.ExchangeEndpoint = Blank(parse.GetValue(exchangeEndpoint));
            record.ExchangeMode = parse.GetValue(exchangeMode);
            record.ExchangeApiKeyHeaderName = parse.GetValue(exchangeHeader) ?? record.ExchangeApiKeyHeaderName;
            record.ExchangeApiKeyValuePrefix = parse.GetValue(exchangePrefix) ?? record.ExchangeApiKeyValuePrefix;
            record.ExchangeTokenPath = parse.GetValue(tokenPath) ?? record.ExchangeTokenPath;
            record.ExchangeExpiresInPath = parse.GetValue(expiresPath) ?? record.ExchangeExpiresInPath;
            record.TestEndpoint = Blank(parse.GetValue(testEndpoint));

            var defaults = CredentialRecord.DefaultInjectionFor(credentialKind);
            record.DefaultPlacement = parse.GetValue(placement) ?? defaults.Placement;
            record.DefaultParameterName = parse.GetValue(parameter) ?? (parse.GetValue(placement) is { } chosen
                ? CredentialInjection.DefaultFor(chosen).Name
                : defaults.Name);
            record.DefaultValuePrefix = parse.GetValue(valuePrefix) ?? (parse.GetValue(placement) is { } chosenPrefix
                ? CredentialInjection.DefaultFor(chosenPrefix).ValuePrefix
                : defaults.ValuePrefix);

            if (parse.GetValue(serviceAccountFile) is { } keyFile)
            {
                record.ServiceAccountJson = await File.ReadAllTextAsync(keyFile.FullName, ct);
            }

            if (parse.GetValue(requestBodyFile) is { } bodyFile)
            {
                record.ExchangeRequestBody = await File.ReadAllTextAsync(bodyFile.FullName, ct);
            }

            // The secrets, from stdin. One per credential at most, so a single piped line is
            // always unambiguous about what it is.
            switch (credentialKind)
            {
                case CredentialKind.ApiKey:
                    record.ApiKey = SecretInput.Read("API key: ");
                    break;
                case CredentialKind.ClientCredentials:
                    record.ClientSecret = SecretInput.Read("Client secret: ") ?? "";
                    break;
                case CredentialKind.TokenExchange when record.ExchangeMode == TokenExchangeMode.ApiKey:
                    record.ExchangeApiKey = SecretInput.Read("API key to exchange: ");
                    break;
                case CredentialKind.OAuth2 or CredentialKind.DeviceCode when parse.GetValue(withSecret):
                    record.ClientSecret = SecretInput.Read("Client secret: ") ?? "";
                    break;
            }

            return await client.RunAsync(() => client.PostAsync("credentials", record));
        });

        return command;
    }

    private static CredentialKind ParseKind(System.CommandLine.Parsing.ArgumentResult result)
    {
        var text = result.Tokens.Single().Value.ToLowerInvariant().Replace("-", "").Replace("_", "");

        CredentialKind? kind = text switch
        {
            "oauth2" or "oauth" => CredentialKind.OAuth2,
            "devicecode" or "device" => CredentialKind.DeviceCode,
            "apikey" or "key" => CredentialKind.ApiKey,
            "clientcredentials" or "applogin" => CredentialKind.ClientCredentials,
            "serviceaccount" or "googleserviceaccount" => CredentialKind.GoogleServiceAccount,
            "tokenexchange" or "exchange" => CredentialKind.TokenExchange,
            _ => null,
        };

        if (kind is { } k) return k;

        result.AddError($"Unknown credential kind '{result.Tokens.Single().Value}'. Use oauth2, device-code, api-key, "
                        + "client-credentials, service-account or token-exchange.");
        return default;
    }

    /// <summary>The Credentials tab's preset rules, so a preset means the same thing in both places.</summary>
    private static void ApplyPreset(CredentialRecord record, OAuthProviderPreset preset)
    {
        var isGoogle = ReferenceEquals(preset, OAuthProviderPreset.Google);

        record.Authority = preset.Authority;
        record.AuthorizationEndpoint = preset.AuthorizationEndpointHint;
        record.DeviceAuthorizationEndpoint = preset.DeviceAuthorizationEndpointHint;
        record.Scopes = [.. preset.DefaultScopes];
        record.UsesPkce = preset.UsesPkce;
        record.RequiresIdToken = preset.RequiresIdToken;
        record.IsGoogleProvider = isGoogle;

        // Google discovers its browser-flow token endpoint from the authority; the device flow has
        // no discovery and needs it spelled out.
        record.TokenEndpoint = preset.TokenEndpointHint
                               ?? (isGoogle && record.Kind == CredentialKind.DeviceCode ? "https://oauth2.googleapis.com/token" : null);
    }

    // ---- upstreams ------------------------------------------------------------------------------

    public static Command Upstreams(CommandContext c)
    {
        var name = new Argument<string>("name") { Description = "Upstream name or id." };
        var baseUrl = new Argument<string>("base-url") { Description = "https:// base address requests are forwarded to." };

        return Group("upstreams", "Upstreams: the APIs routes forward to.",
            Leaf("list", "List upstreams.", c, async (_, client, _) =>
            {
                var (rows, code) = await client.GetAsync<List<UpstreamSummary>>("upstreams");
                if (rows is not null && !client.Json)
                {
                    Table.Write(client.Out, rows, "No upstreams.",
                        ("NAME", r => r.Name), ("BASE URL", r => r.BaseUrl), ("ROUTES", r => r.Routes.ToString()));
                }

                return code;
            }),
            Leaf("add", "Add an upstream.", c,
                (parse, client, _) => client.PostAsync("upstreams", new AddUpstreamRequest(parse.GetValue(name)!, parse.GetValue(baseUrl)!)),
                name, baseUrl),
            Leaf("remove", "Delete an upstream.", c,
                (parse, client, _) => client.DeleteAsync($"upstreams/{Escape(parse.GetValue(name)!)}"), name));
    }

    // ---- routes ---------------------------------------------------------------------------------

    public static Command Routes(CommandContext c)
    {
        var prefix = new Argument<string>("prefix") { Description = "Route path prefix (e.g. /weather) or id." };

        var upstream = new Option<string>("--upstream") { Description = "Upstream name or id to forward to.", Required = true };
        var credential = new Option<string?>("--credential") { Description = "Credential to attach. Omit for an unauthenticated hop." };
        var keepPrefix = new Option<bool>("--keep-prefix") { Description = "Forward the path prefix too, instead of stripping it." };
        var placement = new Option<CredentialPlacement?>("--placement") { Description = "Override where the credential is sent." };
        var parameter = new Option<string?>("--parameter") { Description = "Override the header, query or body field name." };
        var valuePrefix = new Option<string?>("--value-prefix") { Description = "Override the text before the value." };
        var keyDays = KeyDaysOption();

        var add = Leaf("add", "Add a route. It gets its own proxy key.", c,
            (parse, client, _) => client.PostAsync("routes", new AddRouteRequest(
                parse.GetValue(prefix)!, parse.GetValue(upstream)!, !parse.GetValue(keepPrefix), parse.GetValue(credential),
                parse.GetValue(placement), parse.GetValue(parameter), parse.GetValue(valuePrefix), parse.GetValue(keyDays))),
            prefix, upstream, credential, keepPrefix, placement, parameter, valuePrefix, keyDays);

        return Group("routes", "Routes: path prefixes on the proxy, each forwarding to an upstream with its credentials.",
            Leaf("list", "List routes.", c, async (_, client, _) =>
            {
                var (rows, code) = await client.GetAsync<List<RouteSummary>>("routes");
                if (rows is not null && !client.Json)
                {
                    Table.Write(client.Out, rows, "No routes.",
                        ("PREFIX", r => r.PathPrefix), ("UPSTREAM", r => r.Upstream), ("ENABLED", r => YesNo(r.Enabled)),
                        ("CREDENTIALS", r => r.Credentials), ("KEY", r => r.KeyExpiry));
                }

                return code;
            }),
            add,
            ById("remove", "Delete a route.", c, prefix, ResolveRouteAsync, (client, id) => client.DeleteAsync($"routes/{id}")),
            ById("enable", "Serve a route again.", c, prefix, ResolveRouteAsync, (client, id) => client.PostAsync($"routes/{id}/enable")),
            ById("disable", "Stop serving a route without deleting it.", c, prefix, ResolveRouteAsync, (client, id) => client.PostAsync($"routes/{id}/disable")),
            KeyGroup(c, "routes", prefix, ResolveRouteAsync));
    }

    /// <summary>
    /// A prefix is a path, and its slashes do not survive as one URL segment — so routes are looked
    /// up by prefix here and addressed by id on the wire.
    /// </summary>
    private static async Task<string?> ResolveRouteAsync(AdminClient client, string key)
    {
        if (Guid.TryParse(key, out _)) return key;

        var (routes, _) = await client.GetAsync<List<RouteSummary>>("routes", print: false);
        if (routes is null) return null;

        var wanted = (key.StartsWith('/') ? key : "/" + key).TrimEnd('/');
        var match = routes.FirstOrDefault(r => string.Equals(r.PathPrefix.TrimEnd('/'), wanted, StringComparison.OrdinalIgnoreCase));

        return match?.Id.ToString() ?? key;
    }

    // ---- MCP sources ----------------------------------------------------------------------------

    public static Command Sources(CommandContext c)
    {
        var name = new Argument<string>("name") { Description = "Display name for the source." };
        var key = new Argument<string>("source") { Description = "Source alias, name or id." };
        var url = new Option<string?>("--url") { Description = "Remote MCP server URL." };
        var route = new Option<string?>("--route") { Description = "A route on this proxy that leads to an MCP server." };
        var bridge = new Option<string?>("--bridge") { Description = "An API bridge on this proxy." };
        var alias = new Option<string?>("--alias") { Description = "Prefix for its tool names. Defaults from the name." };
        var transport = new Option<McpTransportPreference>("--transport")
        {
            Description = "Auto, StreamableHttp or Sse.",
            DefaultValueFactory = _ => McpTransportPreference.Auto,
        };
        var only = new Option<string?>("--source") { Description = "Refresh only this source." };

        var add = new Command("add", "Add an MCP source: exactly one of --url, --route or --bridge.") { name, url, route, bridge, alias, transport };
        add.SetAction(async (parse, ct) =>
        {
            using var client = c.Client(parse);
            var targets = new[] { parse.GetValue(url), parse.GetValue(route), parse.GetValue(bridge) }.Count(t => t is not null);
            if (targets != 1)
            {
                c.Error.WriteLine("error: give exactly one of --url, --route or --bridge.");
                return ExitCodes.Usage;
            }

            var kind = parse.GetValue(url) is not null ? McpSourceKind.RemoteUrl
                : parse.GetValue(route) is not null ? McpSourceKind.ProxyRoute
                : McpSourceKind.ApiBridge;

            return await client.RunAsync(() => client.PostAsync("sources", new AddSourceRequest(
                parse.GetValue(name)!, kind, parse.GetValue(alias), parse.GetValue(route), parse.GetValue(url),
                parse.GetValue(bridge), parse.GetValue(transport))));
        });

        return Group("sources", "MCP sources: the servers funnels pool tools from.",
            Leaf("list", "List sources and what each last reported.", c, async (_, client, _) =>
            {
                var (rows, code) = await client.GetAsync<List<SourceSummary>>("sources");
                if (rows is not null && !client.Json)
                {
                    Table.Write(client.Out, rows, "No sources.",
                        ("ALIAS", r => r.Alias), ("NAME", r => r.Name), ("KIND", r => r.Kind.ToString()), ("TARGET", r => r.Target),
                        ("ENABLED", r => YesNo(r.Enabled)), ("CATALOG", r => r.Catalog));
                }

                return code;
            }),
            add,
            Leaf("remove", "Delete a source, and its place in every funnel.", c,
                (parse, client, _) => client.DeleteAsync($"sources/{Escape(parse.GetValue(key)!)}"), key),
            Leaf("refresh", "Connect to the sources and list what they offer.", c,
                (parse, client, _) => client.PostAsync(parse.GetValue(only) is { } one ? $"sources/refresh?source={Escape(one)}" : "sources/refresh"),
                only),
            Leaf("enable", "Use a source again.", c, (parse, client, _) => client.PostAsync($"sources/{Escape(parse.GetValue(key)!)}/enable"), key),
            Leaf("disable", "Stop using a source without deleting it.", c, (parse, client, _) => client.PostAsync($"sources/{Escape(parse.GetValue(key)!)}/disable"), key));
    }

    // ---- funnels --------------------------------------------------------------------------------

    public static Command Funnels(CommandContext c)
    {
        var name = new Argument<string>("name") { Description = "Display name for the funnel." };
        var funnel = new Argument<string>("funnel") { Description = "Funnel slug or id." };
        var source = new Argument<string>("source") { Description = "Source alias, name or id." };
        var slug = new Option<string?>("--slug") { Description = "Path segment under /mcp. Defaults from the name." };
        var include = new Option<string[]>("--only") { Description = "Expose only these tools (repeatable).", AllowMultipleArgumentsPerToken = true };
        var exclude = new Option<string[]>("--except") { Description = "Expose every tool but these (repeatable).", AllowMultipleArgumentsPerToken = true };
        var keyDays = KeyDaysOption();

        var sourceAdd = new Command("add", "Add a source to a funnel, or change which of its tools it exposes.") { funnel, source, include, exclude };
        sourceAdd.SetAction(async (parse, ct) =>
        {
            using var client = c.Client(parse);
            var only = parse.GetValue(include) ?? [];
            var except = parse.GetValue(exclude) ?? [];

            if (only.Length > 0 && except.Length > 0)
            {
                c.Error.WriteLine("error: use --only or --except, not both.");
                return ExitCodes.Usage;
            }

            var request = only.Length > 0 ? new FunnelSourceRequest(parse.GetValue(source)!, McpSelectionMode.Include, [.. only])
                : except.Length > 0 ? new FunnelSourceRequest(parse.GetValue(source)!, McpSelectionMode.Exclude, [.. except])
                : new FunnelSourceRequest(parse.GetValue(source)!);

            return await client.RunAsync(() => client.PostAsync($"funnels/{Escape(parse.GetValue(funnel)!)}/sources", request));
        });

        return Group("funnels", "MCP funnels: one endpoint per agent, pooling the sources and tools you allow.",
            Leaf("list", "List funnels and what they pool.", c, async (_, client, _) =>
            {
                var (rows, code) = await client.GetAsync<List<FunnelSummary>>("funnels");
                if (rows is not null && !client.Json)
                {
                    Table.Write(client.Out, rows, "No funnels.",
                        ("SLUG", r => r.Slug), ("NAME", r => r.Name), ("ENABLED", r => YesNo(r.Enabled)),
                        ("SOURCES", r => r.Sources.Count == 0 ? "(none)" : string.Join("; ", r.Sources)), ("KEY", r => r.KeyExpiry));
                }

                return code;
            }),
            Leaf("add", "Add a funnel. It gets its own proxy key.", c,
                (parse, client, _) => client.PostAsync("funnels", new AddFunnelRequest(parse.GetValue(name)!, parse.GetValue(slug), parse.GetValue(keyDays))),
                name, slug, keyDays),
            Leaf("remove", "Delete a funnel.", c, (parse, client, _) => client.DeleteAsync($"funnels/{Escape(parse.GetValue(funnel)!)}"), funnel),
            Leaf("enable", "Serve a funnel again.", c, (parse, client, _) => client.PostAsync($"funnels/{Escape(parse.GetValue(funnel)!)}/enable"), funnel),
            Leaf("disable", "Stop serving a funnel without deleting it.", c, (parse, client, _) => client.PostAsync($"funnels/{Escape(parse.GetValue(funnel)!)}/disable"), funnel),
            Group("source", "Which sources a funnel pools.",
                sourceAdd,
                Leaf("remove", "Take a source out of a funnel.", c,
                    (parse, client, _) => client.DeleteAsync($"funnels/{Escape(parse.GetValue(funnel)!)}/sources/{Escape(parse.GetValue(source)!)}"),
                    funnel, source)),
            KeyGroup(c, "funnels", funnel, (_, k) => Task.FromResult<string?>(Escape(k))));
    }

    // ---- API bridges ----------------------------------------------------------------------------

    public static Command Bridges(CommandContext c)
    {
        var name = new Argument<string>("name") { Description = "Display name for the bridge." };
        var file = new Argument<FileInfo>("file") { Description = "A RavensPort manifest (.json), or an OpenAPI document with --openapi." };
        var bridge = new Argument<string>("bridge") { Description = "Bridge slug or id." };
        var route = new Option<string>("--route") { Description = "The route on this proxy the bridge calls.", Required = true };
        var slug = new Option<string?>("--slug") { Description = "Path segment under /api-mcp. Defaults from the name." };
        var openApi = new Option<bool>("--openapi") { Description = "The file is an OpenAPI document; convert it to a manifest." };
        var operations = new Option<string[]>("--operation")
        {
            Description = "With --openapi, include only this operation, as \"GET /path\" (repeatable). See `bridges operations`.",
            AllowMultipleArgumentsPerToken = true,
        };
        var keyDays = KeyDaysOption();

        var import = new Command("import", "Add an API bridge from a manifest or an OpenAPI document.") { name, file, route, slug, openApi, operations, keyDays };
        import.SetAction(async (parse, ct) =>
        {
            using var client = c.Client(parse);
            var path = parse.GetValue(file)!;
            var text = await File.ReadAllTextAsync(path.FullName, ct);
            var manifestJson = text;
            var origin = $"file: {path.Name}";

            if (parse.GetValue(openApi))
            {
                var picked = parse.GetValue(operations) ?? [];
                HashSet<(string Path, string Method)>? includeOnly = null;

                if (picked.Length > 0)
                {
                    includeOnly = [];
                    foreach (var op in picked)
                    {
                        var parts = op.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                        if (parts.Length != 2)
                        {
                            c.Error.WriteLine($"error: '{op}' is not \"METHOD /path\".");
                            return ExitCodes.Usage;
                        }

                        includeOnly.Add((parts[1], parts[0].ToLowerInvariant()));
                    }
                }

                var result = OpenApiImporter.Convert(text, path.Name, includeOnly);
                if (result.Error is { } importError)
                {
                    c.Error.WriteLine($"error: {importError}");
                    return ExitCodes.Refused;
                }

                foreach (var warning in result.Warnings) c.Error.WriteLine($"warning: {warning}");
                manifestJson = result.ManifestJson!;
                origin = $"OpenAPI: {path.Name}";
            }

            return await client.RunAsync(() => client.PostAsync("bridges", new ImportBridgeRequest(
                parse.GetValue(name)!, parse.GetValue(route)!, manifestJson, parse.GetValue(slug), origin, parse.GetValue(keyDays))));
        });

        var operationsCommand = new Command("operations", "List the operations in an OpenAPI document, for --operation.") { file };
        operationsCommand.SetAction(async (parse, ct) =>
        {
            var path = parse.GetValue(file)!;
            var (error, ops, _) = OpenApiImporter.Discover(await File.ReadAllTextAsync(path.FullName, ct));
            if (error is not null)
            {
                c.Error.WriteLine($"error: {error}");
                return ExitCodes.Refused;
            }

            Table.Write(c.Output, ops, "No operations.",
                ("OPERATION", o => $"{o.Method.ToUpperInvariant()} {o.Path}"), ("ID", o => o.OperationId ?? ""), ("SUMMARY", o => o.Summary ?? ""));
            return ExitCodes.Ok;
        });

        return Group("bridges", "API bridges: an API you already proxy, served as an MCP endpoint.",
            Leaf("list", "List API bridges.", c, async (_, client, _) =>
            {
                var (rows, code) = await client.GetAsync<List<BridgeSummary>>("bridges");
                if (rows is not null && !client.Json)
                {
                    Table.Write(client.Out, rows, "No API bridges.",
                        ("SLUG", r => r.Slug), ("NAME", r => r.Name), ("ROUTE", r => r.Route), ("TOOLS", r => r.Tools.ToString()),
                        ("ENABLED", r => YesNo(r.Enabled)), ("KEY", r => r.KeyExpiry));
                }

                return code;
            }),
            import,
            operationsCommand,
            Leaf("remove", "Delete an API bridge, and any funnel source that exposed it.", c,
                (parse, client, _) => client.DeleteAsync($"bridges/{Escape(parse.GetValue(bridge)!)}"), bridge),
            Leaf("enable", "Serve a bridge again.", c, (parse, client, _) => client.PostAsync($"bridges/{Escape(parse.GetValue(bridge)!)}/enable"), bridge),
            Leaf("disable", "Stop serving a bridge without deleting it.", c, (parse, client, _) => client.PostAsync($"bridges/{Escape(parse.GetValue(bridge)!)}/disable"), bridge),
            KeyGroup(c, "bridges", bridge, (_, k) => Task.FromResult<string?>(Escape(k))));
    }

    // ---- settings -------------------------------------------------------------------------------

    public static Command Settings(CommandContext c)
    {
        var setting = new Argument<string>("setting") { Description = "port, funnel, bridge or mtls." };
        var value = new Argument<string>("value") { Description = "A port number, or on/off." };
        var output = new Option<FileInfo>("--output") { Description = "Where to write the client certificate (.pfx).", Required = true };

        var set = Leaf("set", "Change a setting.", c, (parse, client, _) =>
        {
            var name = parse.GetValue(setting)!.ToLowerInvariant();
            var text = parse.GetValue(value)!.ToLowerInvariant();

            bool? Switch() => text switch
            {
                "on" or "true" or "yes" or "1" => true,
                "off" or "false" or "no" or "0" => false,
                _ => null,
            };

            SettingsPatch? patch = name switch
            {
                "port" when int.TryParse(text, out var port) => new SettingsPatch(ListenPort: port),
                "funnel" when Switch() is { } on => new SettingsPatch(McpFunnelEnabled: on),
                "bridge" or "bridges" when Switch() is { } on => new SettingsPatch(McpApiBridgeEnabled: on),
                "mtls" when Switch() is { } on => new SettingsPatch(MtlsEnabled: on),
                _ => null,
            };

            if (patch is null)
            {
                c.Error.WriteLine($"error: cannot set '{name}' to '{text}'. Use: port <number>, funnel on|off, bridge on|off, mtls on|off.");
                return Task.FromResult(ExitCodes.Usage);
            }

            return client.PatchAsync("settings", patch);
        }, setting, value);

        var generate = new Command("generate",
            "Generate a new mTLS client certificate, store it in the vault and write it out for clients. "
            + "The password is read from stdin.") { output };
        generate.SetAction(async (parse, ct) =>
        {
            using var client = c.Client(parse);
            var password = SecretInput.Read("Password for the new certificate: ");
            if (string.IsNullOrEmpty(password))
            {
                c.Error.WriteLine("error: a password is required. Clients need it to load the certificate.");
                return ExitCodes.Usage;
            }

            return await client.RunAsync(async () =>
            {
                var (reply, code) = await client.SendForAsync<GenerateCertificateReply>(
                    HttpMethod.Post, "settings/mtls/generate", new GenerateCertificateRequest(password));
                if (reply is null) return code;

                var target = parse.GetValue(output)!;
                await File.WriteAllBytesAsync(target.FullName, Convert.FromBase64String(reply.PfxBase64), ct);
                if (!client.Json) client.Out.WriteLine($"{reply.Message}\nWrote {target.FullName}.");
                return code;
            });
        });

        return Group("settings", "Proxy settings.",
            Leaf("get", "Show the settings.", c, async (_, client, _) =>
            {
                var (s, code) = await client.GetAsync<SettingsReply>("settings");
                if (s is not null && !client.Json)
                {
                    client.Out.WriteLine($"port    {s.ListenPort}");
                    client.Out.WriteLine($"funnel  {OnOff(s.McpFunnelEnabled)}");
                    client.Out.WriteLine($"bridge  {OnOff(s.McpApiBridgeEnabled)}");
                    client.Out.WriteLine($"mtls    {OnOff(s.MtlsEnabled)}{(s.HasCertificate ? "" : " (no certificate yet)")}");
                }

                return code;
            }),
            set,
            Group("mtls", "Client certificates.", generate));
    }

    // ---- building blocks ------------------------------------------------------------------------

    private static Command Group(string name, string description, params Command[] children)
    {
        var group = new Command(name, description);
        foreach (var child in children) group.Subcommands.Add(child);
        return group;
    }

    /// <summary>A command that makes admin requests, with "nobody is listening" handled once.</summary>
    private static Command Leaf(
        string name, string description, CommandContext c,
        Func<ParseResult, AdminClient, CancellationToken, Task<int>> run, params Symbol[] symbols)
    {
        var command = new Command(name, description);

        foreach (var symbol in symbols)
        {
            switch (symbol)
            {
                case Argument argument: command.Arguments.Add(argument); break;
                case Option option: command.Options.Add(option); break;
            }
        }

        command.SetAction(async (parse, ct) =>
        {
            using var client = c.Client(parse);
            return await client.RunAsync(() => run(parse, client, ct));
        });

        return command;
    }

    private static Command ById(
        string name, string description, CommandContext c, Argument<string> key,
        Func<AdminClient, string, Task<string?>> resolve, Func<AdminClient, string, Task<int>> act) =>
        Leaf(name, description, c, async (parse, client, _) =>
            await resolve(client, parse.GetValue(key)!) is { } id ? await act(client, id) : ExitCodes.Refused, key);

    private static Command KeyGroup(CommandContext c, string collection, Argument<string> key, Func<AdminClient, string, Task<string?>> resolve) =>
        Group("key", "The endpoint's own proxy key.",
            ById("show", "Print the proxy key, for configuring a client.", c, key, resolve, async (client, id) =>
            {
                var (info, code) = await client.GetAsync<KeyInfo>($"{collection}/{id}/key");
                if (info is not null && !client.Json)
                {
                    client.Out.WriteLine(info.Value);
                    Console.Error.WriteLine($"({info.Expiry})");
                }

                return code;
            }),
            ById("rotate", "Replace the proxy key. The old one stops working at once.", c, key, resolve,
                (client, id) => client.PostAsync($"{collection}/{id}/key/rotate")));

    private static Option<int?> KeyDaysOption() =>
        new("--key-days") { Description = "Days until the endpoint's proxy key expires. Omit for a key that never expires." };

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string OnOff(bool value) => value ? "on" : "off";

    private static string YesNo(bool value) => value ? "yes" : "no";
}
