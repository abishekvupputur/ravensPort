using System.Diagnostics;
using System.Net;
using IdentityModel.OidcClient.Browser;
using RavensPort.Core.Models;

namespace RavensPort.Core.Auth;

/// <summary>
/// Provider-agnostic loopback redirect capture for the RFC 8252 installed-app OAuth2 flow.
/// Listens on a fixed localhost port so the redirect URI is stable and can be registered
/// up-front in a provider's console, opens the system browser to the consent screen, and
/// waits for the redirect to land on a local HttpListener.
/// </summary>
public sealed class LoopbackBrowser : IBrowser
{
    private const int RedirectPort = 51005;

    /// <summary>The single, stable redirect URI used for every non-Google provider.</summary>
    public static readonly string StaticRedirectUri = $"http://127.0.0.1:{RedirectPort}/callback/";

    /// <summary>
    /// The redirect port is fixed, so two overlapping sign-ins would collide on it
    /// (HttpListener fails with ERROR_ALREADY_EXISTS). Serialise flows in-process and fail
    /// the second one with an explanation instead of a raw Win32 error.
    /// </summary>
    private static readonly SemaphoreSlim FlowGate = new(1, 1);

    public string RedirectUri => StaticRedirectUri;

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        if (!await FlowGate.WaitAsync(TimeSpan.Zero, cancellationToken))
        {
            return new BrowserResult
            {
                ResultType = BrowserResultType.UnknownError,
                Error = "Another sign-in is already in progress. Finish or cancel it in the browser, then try again.",
            };
        }

        // Created here (not in the constructor) and always disposed below, so an abandoned
        // or failed flow can never leave the fixed port bound for the next attempt.
        var listener = new HttpListener();
        listener.Prefixes.Add(StaticRedirectUri);

        try
        {
            try
            {
                listener.Start();
            }
            catch (HttpListenerException ex)
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.UnknownError,
                    Error = $"Could not listen on {StaticRedirectUri} ({ex.Message}). "
                          + "Another RavensPort instance or another program is using that port.",
                };
            }

            // UseShellExecute means Windows resolves whatever this string is — a registered
            // protocol handler, a UNC path, an executable — not necessarily a browser. The URL
            // is built from user-editable endpoint config, so check the scheme before handing
            // it to the shell.
            if (!UrlValidation.IsSafeToOpenInBrowser(options.StartUrl, out var startUri))
            {
                return new BrowserResult
                {
                    ResultType = BrowserResultType.UnknownError,
                    Error = "Refusing to open the authorization URL: it is not an http/https address. "
                          + "Check this credential's authorization endpoint.",
                };
            }

            // The parsed form, not options.StartUrl: what gets launched is then exactly what the
            // scheme check inspected.
            Process.Start(new ProcessStartInfo(startUri.AbsoluteUri) { UseShellExecute = true });

            var timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : TimeSpan.FromMinutes(5);
            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                return new BrowserResult
                {
                    ResultType = timeoutCts.IsCancellationRequested ? BrowserResultType.Timeout : BrowserResultType.UserCancel,
                };
            }

            // OidcClient parses the redirect itself and reports the failure, but it never sees the
            // browser: what the user is left looking at is decided here, so read the error out too.
            var error = context.Request.QueryString["error"];
            if (string.IsNullOrWhiteSpace(error))
            {
                await CallbackPage.WriteSuccessAsync(context.Response, cancellationToken);
            }
            else
            {
                await CallbackPage.WriteFailureAsync(
                    context.Response, error, context.Request.QueryString["error_description"], cancellationToken);
            }

            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                // AbsoluteUri is the documented fully-escaped form; ToString() is a display
                // form that unescapes some characters. OidcClient does its own decoding, so
                // it must receive the escaped URI exactly once.
                Response = context.Request.Url!.AbsoluteUri,
            };
        }
        catch (Exception ex)
        {
            return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = ex.Message };
        }
        finally
        {
            // Close() both stops the listener and releases the HTTP.SYS registration.
            listener.Close();
            FlowGate.Release();
        }
    }
}
