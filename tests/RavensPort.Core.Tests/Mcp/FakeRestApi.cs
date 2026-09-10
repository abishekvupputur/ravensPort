using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RavensPort.Core.Tests.Mcp;

/// <summary>
/// A plain REST upstream that records what it was asked for.
///
/// The API bridge's whole job is turning a tool call into one HTTP request, so the assertions that
/// matter are about the request that arrives: which path, which query, which headers, which body.
/// A mock of the HTTP client would prove the builder works; this proves the call actually leaves
/// the app the way the manifest said, through the guard and YARP and the credential transform.
/// </summary>
internal sealed class FakeRestApi : IAsyncDisposable
{
    private readonly WebApplication _app;

    private FakeRestApi(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    public string Url { get; }

    /// <summary>Every request that arrived, in order.</summary>
    public List<RecordedRequest> Received { get; } = [];

    /// <summary>What to answer with. Defaults to 200 and a small JSON body.</summary>
    public int StatusCode { get; set; } = StatusCodes.Status200OK;

    public string ResponseBody { get; set; } = """{"ok":true}""";

    public string? RedirectTo { get; set; }

    internal sealed record RecordedRequest(
        string Method,
        string Path,
        string Query,
        IReadOnlyDictionary<string, string> Headers,
        string Body)
    {
        public string? Header(string name) => Headers.TryGetValue(name, out var value) ? value : null;
    }

    public static async Task<FakeRestApi> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        var app = builder.Build();
        FakeRestApi? instance = null;

        app.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            instance!.Received.Add(new RecordedRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "",
                context.Request.QueryString.Value ?? "",
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                body));

            if (instance.RedirectTo is { } location)
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = location;
                return;
            }

            context.Response.StatusCode = instance.StatusCode;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(instance.ResponseBody);
        });

        await app.StartAsync();

        var url = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();

        instance = new FakeRestApi(app, url);
        return instance;
    }

    /// <summary>The one request that arrived, asserting that exactly one did.</summary>
    public RecordedRequest Single() => Assert.Single(Received);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
