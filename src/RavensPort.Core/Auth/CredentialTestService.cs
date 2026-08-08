using RavensPort.Core.Net;
using System.Net;
using Microsoft.AspNetCore.WebUtilities;
using RavensPort.Core.Diagnostics;
using RavensPort.Core.Models;

namespace RavensPort.Core.Auth;

/// <summary>What a credential test found.</summary>
/// <param name="Success">True only for a 200 from the configured endpoint.</param>
/// <param name="StatusCode">The upstream's status, or null when the request never completed.</param>
/// <param name="Message">One line suitable for the UI footer. Never contains the secret.</param>
public sealed record CredentialTestResult(bool Success, int? StatusCode, string Message);

/// <summary>
/// Sends one authenticated GET to a credential's configured test endpoint and reports whether it
/// answered 200.
///
/// This exists because a static API key is otherwise unverifiable at the moment it is entered.
/// An OAuth grant proves itself during the browser flow — a wrong client secret cannot complete
/// it — but nothing checks a pasted key, so the first evidence of a typo is a 401 from a real
/// request some hours later, by which time it looks like an upstream problem.
///
/// Deliberately narrow: GET only, one request, 200 or nothing. A test that accepted "any 2xx" or
/// followed redirects would pass against a sign-in page, which is precisely the failure it is
/// meant to catch.
/// </summary>
public sealed class CredentialTestService : IDisposable
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private readonly AccessTokenProvider _accessTokenProvider;
    private readonly ActivityLog _activityLog;
    private readonly HttpClient _httpClient;

    public CredentialTestService(AccessTokenProvider accessTokenProvider, ActivityLog activityLog)
    {
        _accessTokenProvider = accessTokenProvider;
        _activityLog = activityLog;

        // Redirects are not followed on purpose. An API that rejects an unauthenticated request
        // by bouncing it to a login page would otherwise return 200 for the login page, and the
        // test would pass for a key that does not work.
        _httpClient = new HttpClient(HappyEyeballs.CreateHandler(h => h.AllowAutoRedirect = false))
        {
            Timeout = RequestTimeout,
        };
    }

    public async Task<CredentialTestResult> TestAsync(CredentialRecord credential, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(credential.TestEndpoint))
        {
            return new CredentialTestResult(false, null,
                "No test endpoint set for this credential. Add a URL that answers 200 to an authenticated GET.");
        }

        if (CredentialValidation.ValidateTestEndpoint(credential.TestEndpoint) is { } urlError)
        {
            return new CredentialTestResult(false, null, urlError);
        }

        var injection = credential.ToDefaultInjection();

        // A GET carries no body, so there is nothing for a body placement to be written into.
        // Inventing a body would test a request shape the proxy never sends.
        if (injection.Placement == CredentialPlacement.Body)
        {
            return new CredentialTestResult(false, null,
                "Body placement cannot be tested — the test request is a GET, which has no body. "
                + "Set this credential's default placement to a header or query parameter to test it.");
        }

        if (RouteValidation.ValidateCredentialInjection(
                injection.Placement, injection.Name, injection.ValuePrefix) is { } injectionError)
        {
            return new CredentialTestResult(false, null, injectionError);
        }

        var secret = await _accessTokenProvider.GetAccessTokenAsync(credential.Id, ct);
        if (secret is null)
        {
            return new CredentialTestResult(false, null, credential.Kind == CredentialKind.ApiKey
                ? $"'{credential.Name}' has no API key stored."
                : $"'{credential.Name}' is not connected — authorize it first.");
        }

        // The same check the editor applies, repeated here because a store written by hand (or
        // by an older build) can hold a key with a newline in it, and putting that in a header
        // would split the request at the upstream.
        if (CredentialValidation.ValidateApiKey(secret) is { } secretError && credential.Kind == CredentialKind.ApiKey)
        {
            return new CredentialTestResult(false, null, secretError);
        }

        return await SendAsync(credential, injection, secret, ct);
    }

    private async Task<CredentialTestResult> SendAsync(
        CredentialRecord credential, CredentialInjection injection, string secret, CancellationToken ct)
    {
        var value = injection.FormatValue(secret);
        var url = credential.TestEndpoint!.Trim();

        using var request = new HttpRequestMessage(HttpMethod.Get, injection.Placement == CredentialPlacement.Query
            ? QueryHelpers.AddQueryString(url, injection.Name, value)
            : url);

        if (injection.Placement == CredentialPlacement.Header &&
            !request.Headers.TryAddWithoutValidation(injection.Name, value))
        {
            return new CredentialTestResult(false, null,
                $"'{injection.Name}' could not be set as a request header.");
        }

        // Endpoint only, never the query string — a query placement puts the secret in it.
        var loggedTarget = new Uri(url).GetLeftPart(UriPartial.Path);
        _activityLog.Log($"TEST '{credential.Name}' -> GET {loggedTarget} [{injection.Placement.ToString().ToLowerInvariant()} {injection.Name}]");

        try
        {
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var status = (int)response.StatusCode;

            var result = status == (int)HttpStatusCode.OK
                ? new CredentialTestResult(true, status, $"'{credential.Name}' works — {loggedTarget} answered 200.")
                : new CredentialTestResult(false, status, Explain(credential, status, response.StatusCode, loggedTarget));

            _activityLog.Log($"TEST '{credential.Name}' <- {status} {(result.Success ? "OK" : "FAILED")}");
            return result;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            _activityLog.Log($"TEST '{credential.Name}' FAILED — timed out after {RequestTimeout.TotalSeconds:0}s");
            return new CredentialTestResult(false, null,
                $"'{credential.Name}' test timed out after {RequestTimeout.TotalSeconds:0}s — {loggedTarget} did not answer.");
        }
        catch (HttpRequestException ex)
        {
            // The endpoint being unreachable says nothing about the key, and reporting it as a
            // bad credential would send someone off regenerating a key that was fine.
            _activityLog.LogError($"TEST '{credential.Name}' could not reach {loggedTarget}", ex);
            return new CredentialTestResult(false, null,
                $"Could not reach {loggedTarget}: {ex.Message} (this says nothing about the credential itself).");
        }
    }

    private static string Explain(CredentialRecord credential, int status, HttpStatusCode code, string target) => status switch
    {
        401 or 403 => $"'{credential.Name}' was rejected — {target} answered {status} {code}. "
                      + "The secret, or where it is placed, is wrong.",
        >= 300 and < 400 => $"{target} answered {status} {code} (a redirect, most likely to a sign-in page). "
                            + "Redirects are not followed, because following one would report a login page as success.",
        404 => $"{target} answered 404 — check the test endpoint URL itself, not the credential.",
        _ => $"{target} answered {status} {code}, not 200.",
    };

    public void Dispose() => _httpClient.Dispose();
}
