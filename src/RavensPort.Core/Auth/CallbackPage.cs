using System.Net;
using System.Text;

namespace RavensPort.Core.Auth;

/// <summary>
/// The page a provider's redirect lands on at the end of an OAuth2 sign-in.
///
/// It is the one part of this app a user meets outside the app itself, arriving in their browser
/// straight after they typed a password into Google or Nextcloud — the moment they are most
/// entitled to ask what just happened to their credentials. So it says so: where the tokens went,
/// that the page came from their own machine, and that nothing is running on it.
///
/// Both outcomes get a page. A provider that redirects with <c>?error=access_denied</c> used to be
/// congratulated for a completed authorization, leaving the user to discover in the app that
/// nothing had happened.
///
/// Served by <see cref="HttpListener"/> rather than by the proxy, so it cannot reference files:
/// the logo is embedded in this assembly and inlined as a data URI, and there is no script, no
/// font, and no stylesheet to fetch. A browser rendering this page makes no network request at all.
/// </summary>
internal static class CallbackPage
{
    private const string LogoResourceName = "RavensPort.Core.Assets.logo.png";

    /// <summary>
    /// What a provider is allowed to put on this page. Long enough for a real
    /// <c>error_description</c>, short enough that a hostile one cannot flood the layout.
    /// </summary>
    private const int MaxDetailLength = 300;

    /// <summary>
    /// Read once: the flow can run repeatedly in a session, and the bytes never change.
    /// A missing resource yields null rather than throwing — a sign-in that already succeeded
    /// must not fail on its confirmation page.
    /// </summary>
    private static readonly Lazy<string?> LogoDataUri = new(() =>
    {
        using var stream = typeof(CallbackPage).Assembly.GetManifestResourceStream(LogoResourceName);
        if (stream is null) return null;

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return "data:image/png;base64," + Convert.ToBase64String(buffer.ToArray());
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<byte[]> SuccessBytes =
        new(() => Encoding.UTF8.GetBytes(BuildSuccessHtml()), LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// The rendered success page. Exposed so tests can assert what a browser would receive without
    /// standing up an <see cref="HttpListener"/> and driving a whole OAuth flow to reach it.
    /// </summary>
    internal static string Html => Encoding.UTF8.GetString(SuccessBytes.Value);

    /// <summary>The rendered failure page, for the same reason as <see cref="Html"/>.</summary>
    internal static string FailureHtml(string? error, string? errorDescription) =>
        BuildFailureHtml(error, errorDescription);

    /// <summary>Writes the "it worked" page and closes the response.</summary>
    public static Task WriteSuccessAsync(HttpListenerResponse response, CancellationToken cancellationToken = default) =>
        WriteAsync(response, HttpStatusCode.OK, SuccessBytes.Value, cancellationToken);

    /// <summary>
    /// Writes the "it did not work" page, quoting whatever the provider said.
    ///
    /// Still HTTP 200: the request itself was served fine, and a status code the user cannot see
    /// only invites the browser to replace this explanation with its own error page.
    /// </summary>
    public static Task WriteFailureAsync(
        HttpListenerResponse response,
        string? error,
        string? errorDescription = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(BuildFailureHtml(error, errorDescription));
        return WriteAsync(response, HttpStatusCode.OK, bytes, cancellationToken);
    }

    /// <summary>
    /// The charset is stated explicitly: without it browsers fall back to the system codepage and
    /// the em dashes in the copy arrive as mojibake, which on a page whose whole job is to look
    /// trustworthy is worse than it sounds.
    ///
    /// <see cref="HttpListenerResponse.KeepAlive"/> is turned off, and that is load-bearing rather
    /// than tidiness. Both callers serve exactly one request and then close their listener the
    /// instant the flow returns — which is immediately, since writing this page is the last thing
    /// they do. On a kept-alive connection the client is still waiting on the socket at that
    /// moment, and closing the listener destroys the HTTP.SYS queue underneath it: the response is
    /// truncated mid-body and the browser renders a blank page, having just completed a sign-in
    /// that in fact succeeded. Measured at roughly one attempt in fifty locally, which is exactly
    /// often enough to look like a mystery. Announcing the close makes the end of the message part
    /// of the message, so the client is never relying on the listener outliving it.
    /// </summary>
    private static async Task WriteAsync(
        HttpListenerResponse response,
        HttpStatusCode status,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        response.StatusCode = (int)status;
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        response.KeepAlive = false;

        await response.OutputStream.WriteAsync(bytes, cancellationToken);
        response.OutputStream.Close();

        // Completes the response rather than only the stream — the two are not the same, and this
        // one does not return until the body has been handed over in full.
        response.Close();
    }

    /// <summary>Said on both pages: true either way, and the reassurance is the point.</summary>
    private static readonly string[] LocalPageFacts =
    [
        "<li>This page was served by <strong>RavensPort itself</strong>, from your own machine. Nothing about it was fetched from the internet.</li>",
        "<li>It runs <strong>no scripts</strong> and sets no cookies. Closing the tab ends it.</li>",
    ];

    private static string BuildSuccessHtml() => BuildHtml(
        title: "Authorization complete",
        lede: "RavensPort has the access it was granted. You can close this tab and go back to the app.",
        badge: """
            <div class="badge badge-ok" role="img" aria-label="Success">
              <svg viewBox="0 0 24 24" width="26" height="26" aria-hidden="true">
                <path d="M4 12.5 9.5 18 20 7" fill="none" stroke="currentColor" stroke-width="2.6"
                      stroke-linecap="round" stroke-linejoin="round"/>
              </svg>
            </div>
            """,
        facts:
        [
            "<li>The access and refresh tokens went <strong>straight into your password manager vault</strong> — 1Password or Proton Pass, whichever you connected. No copy is kept on this PC.</li>",
            .. LocalPageFacts,
        ]);

    private static string BuildFailureHtml(string? error, string? errorDescription)
    {
        var quoted = Quote(error, errorDescription);

        return BuildHtml(
            title: "Authorization not completed",
            lede: error is not null && error.Equals("access_denied", StringComparison.OrdinalIgnoreCase)
                ? "The request was declined, so nothing was granted. You can close this tab and try again from RavensPort."
                : "The provider ended the sign-in without granting access. You can close this tab and try again from RavensPort.",
            badge: """
                <div class="badge badge-bad" role="img" aria-label="Failed">
                  <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
                    <path d="M6 6l12 12M18 6L6 18" fill="none" stroke="currentColor" stroke-width="2.6"
                          stroke-linecap="round"/>
                  </svg>
                </div>
                """,
            facts:
            [
                .. quoted,
                "<li><strong>No tokens were issued</strong>, and nothing was written to your password manager vault.</li>",
                .. LocalPageFacts,
            ]);
    }

    /// <summary>
    /// The provider's own words, when it left any. Everything here arrives in a query string from
    /// a remote server, so it is encoded before it goes anywhere near the markup — an
    /// <c>error_description</c> is as untrusted as any other input.
    /// </summary>
    private static string[] Quote(string? error, string? errorDescription)
    {
        var detail = Clean(errorDescription) is { Length: > 0 } described
            ? described
            : Clean(error);

        return detail.Length == 0
            ? []
            : [$"""<li>The provider said: <span class="quote">{WebUtility.HtmlEncode(detail)}</span></li>"""];
    }

    private static string Indent(string block, int spaces)
    {
        var pad = new string(' ', spaces);
        return string.Join('\n', block.Split('\n').Select(line => pad + line.TrimEnd('\r')));
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        // Newlines and control characters would break out of the list item's single line; the
        // cap keeps a pathological description from pushing the rest of the page off-screen.
        var collapsed = new string([.. value.Select(c => char.IsControl(c) ? ' ' : c)]);
        collapsed = string.Join(' ', collapsed.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        return collapsed.Length <= MaxDetailLength
            ? collapsed
            : string.Concat(collapsed.AsSpan(0, MaxDetailLength - 1), "…");
    }

    private static string BuildHtml(string title, string lede, string badge, string[] facts)
    {
        var logo = LogoDataUri.Value is { } uri
            ? $"""<img class="logo" src="{uri}" alt="RavensPort" width="88" height="88">"""
            : "";

        // Blocks are written at their own indentation above; line them up with where they land so
        // a curious user reading the source finds a tidy page rather than a generated-looking one.
        var badgeBlock = Indent(badge, 6);
        var factsBlock = Indent(string.Join('\n', facts), 6);

        // $$ raw string: interpolation is {{...}}, so the CSS braces below are literal.
        return $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex">
            <title>{{title}} — RavensPort</title>
            <style>
              :root {
                --bg: #0e0e10;
                --card: #17171a;
                --edge: #27272d;
                --text: #f2f2f3;
                --muted: #a1a1a8;
                --accent: #f0a429;
                --ok: #43b581;
                --ok-bg: #16281f;
                --bad: #e5534b;
                --bad-bg: #2b1817;
                --quote-bg: #202027;
              }
              @media (prefers-color-scheme: light) {
                :root {
                  --bg: #f4f4f5;
                  --card: #ffffff;
                  --edge: #e3e3e6;
                  --text: #17171a;
                  --muted: #5f5f68;
                  --accent: #b97708;
                  --ok: #1a7f5a;
                  --ok-bg: #e4f4ec;
                  --bad: #c0392f;
                  --bad-bg: #fbeae8;
                  --quote-bg: #f1f1f3;
                }
              }
              * { box-sizing: border-box; }
              body {
                margin: 0;
                min-height: 100vh;
                display: flex;
                align-items: center;
                justify-content: center;
                padding: 24px;
                background: var(--bg);
                color: var(--text);
                font-family: "Segoe UI", system-ui, -apple-system, sans-serif;
                line-height: 1.55;
              }
              .card {
                width: 100%;
                max-width: 460px;
                padding: 40px 36px 28px;
                background: var(--card);
                border: 1px solid var(--edge);
                border-radius: 18px;
                text-align: center;
              }
              .mark { position: relative; width: 88px; margin: 0 auto 22px; }
              .logo { width: 88px; height: 88px; border-radius: 18px; display: block; }
              .badge {
                position: absolute;
                right: -10px;
                bottom: -10px;
                width: 40px;
                height: 40px;
                display: flex;
                align-items: center;
                justify-content: center;
                border-radius: 50%;
                border: 3px solid var(--card);
              }
              .badge-ok { background: var(--ok-bg); color: var(--ok); }
              .badge-bad { background: var(--bad-bg); color: var(--bad); }
              h1 {
                margin: 0 0 10px;
                font-size: 21px;
                font-weight: 600;
                letter-spacing: -0.01em;
              }
              .lede { margin: 0 0 26px; color: var(--muted); font-size: 14.5px; }
              .facts {
                margin: 0;
                padding: 20px 0 0;
                border-top: 1px solid var(--edge);
                list-style: none;
                text-align: left;
              }
              .facts li {
                position: relative;
                margin: 0 0 12px;
                padding-left: 26px;
                color: var(--muted);
                font-size: 13.5px;
              }
              .facts li:last-child { margin-bottom: 0; }
              .facts li::before {
                content: "";
                position: absolute;
                left: 6px;
                top: 8px;
                width: 6px;
                height: 6px;
                border-radius: 50%;
                background: var(--accent);
              }
              .facts strong { color: var(--text); font-weight: 600; }
              .quote {
                display: inline-block;
                margin-top: 4px;
                padding: 2px 8px;
                background: var(--quote-bg);
                border-radius: 6px;
                color: var(--text);
                font-family: Consolas, "Cascadia Mono", monospace;
                font-size: 12.5px;
                overflow-wrap: anywhere;
              }
              footer {
                margin-top: 26px;
                padding-top: 18px;
                border-top: 1px solid var(--edge);
                color: var(--muted);
                font-size: 12px;
              }
              footer code { font-family: Consolas, "Cascadia Mono", monospace; }
            </style>
            </head>
            <body>
              <main class="card">
                <div class="mark">
                  {{logo}}
            {{badgeBlock}}
                </div>
                <h1>{{title}}</h1>
                <p class="lede">{{lede}}</p>
                <ul class="facts">
            {{factsBlock}}
                </ul>
                <footer>RavensPort — local proxy on <code>127.0.0.1</code></footer>
              </main>
            </body>
            </html>
            """;
    }
}
