using Google.Apis.Http;

namespace RavensPort.Core.Net;

/// <summary>
/// Makes the Google client library connect the way the rest of this app does, and give up sooner.
///
/// The library builds its own <see cref="ConfigurableHttpClient"/>, so neither the connect
/// behaviour nor the timeout can be set from outside without replacing the factory. Both needed
/// replacing:
///
/// <list type="bullet">
/// <item>
/// The connect callback, for the reason in <see cref="HappyEyeballs"/> — on a host with a broken
/// IPv6 route this is what turned "refresh this credential" into a hundred seconds of nothing.
/// </item>
/// <item>
/// The timeout. A hundred seconds is <see cref="HttpClient"/>'s default rather than a decision, and
/// it is far too long for something a person clicked. A token refresh is one round trip to a
/// provider that is either answering or is not.
/// </item>
/// </list>
///
/// Implemented by composition rather than by subclassing <see cref="HttpClientFactory"/>: the
/// handler hook is virtual, but <c>CreateHttpClient</c> is not, and the timeout lives on the client.
/// </summary>
internal sealed class RavensPortGoogleHttpClientFactory : Google.Apis.Http.IHttpClientFactory
{
    /// <summary>
    /// Long enough for a slow link and a retry inside the library, short enough that a user who
    /// pressed Refresh gets an answer while they are still looking at the window.
    /// </summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    public static readonly RavensPortGoogleHttpClientFactory Instance = new();

    private readonly HappyEyeballsClientFactory _inner = new();

    public ConfigurableHttpClient CreateHttpClient(CreateHttpClientArgs args)
    {
        var client = _inner.CreateHttpClient(args);
        client.Timeout = RequestTimeout;

        return client;
    }

    /// <summary>Exists only to reach <see cref="HttpClientFactory.CreateHandler"/>, which is virtual.</summary>
    private sealed class HappyEyeballsClientFactory : HttpClientFactory
    {
        protected override HttpMessageHandler CreateHandler(CreateHttpClientArgs args) =>
            HappyEyeballs.CreateHandler();
    }
}
