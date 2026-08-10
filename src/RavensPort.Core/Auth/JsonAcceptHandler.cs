using System.Net.Http.Headers;

namespace RavensPort.Core.Auth;

/// <summary>
/// Asks every back-channel request for JSON.
///
/// OAuth2 says a token response is JSON, so most providers send it unconditionally and this
/// changes nothing for them. GitHub is the exception that makes it necessary: its token endpoint
/// answers <c>application/x-www-form-urlencoded</c> unless the request says otherwise, and the
/// OAuth library then fails to parse a response that arrived perfectly intact — reported as a
/// malformed token response, which sends you looking at the client secret.
///
/// Applied to the whole back channel rather than only to GitHub. The header is what the spec
/// already implies, and one code path is worth more here than a per-provider exception.
/// </summary>
public sealed class JsonAcceptHandler : DelegatingHandler
{
    private static readonly MediaTypeWithQualityHeaderValue Json = new("application/json");

    public JsonAcceptHandler(HttpMessageHandler innerHandler) : base(innerHandler)
    {
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Only when nothing has been asked for already, so a caller that deliberately wants
        // something else is not overruled.
        if (request.Headers.Accept.Count == 0)
        {
            request.Headers.Accept.Add(Json);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
