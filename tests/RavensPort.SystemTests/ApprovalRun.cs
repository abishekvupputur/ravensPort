using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using RavensPort.Core.Models;
using RavensPort.Core.Tests.Mcp;

namespace RavensPort.SystemTests;

/// <summary>
/// The state one approval run carries from stage to stage.
///
/// The run is a sequence, not a set: the vault is empty only before anything is added, mTLS can be
/// switched on only once there is something to protect, and the restart means nothing until there
/// is state that had to survive it. That was once a single test method, which ran the sequence
/// correctly and reported it as one opaque pass or fail -- "Total tests: 1" in CI, with nothing
/// saying which parts had been exercised.
///
/// So the stages are separate tests over a shared fixture, ordered by <see cref="StageOrderer"/>,
/// and this is what they share. Each stage names what it proves; a reader of the CI log sees the
/// list whether or not anything failed.
///
/// <see cref="Abort"/> is what keeps that honest. xUnit will happily run stage 6 after stage 2
/// threw, and the cascade of failures that follows says nothing about the product -- so the first
/// failure records itself here and every later stage reports which prerequisite it is waiting on
/// instead of inventing its own.
///
/// The class is public because xUnit has to construct it as a class fixture; the members that hand
/// out SystemTestHost, the account or a fake server are internal because those types are, and there
/// is nothing outside this assembly to hand them to.
/// </summary>
public sealed class ApprovalRun : IAsyncLifetime
{
    // Fixtures, not credentials.
    public const string ProjectKey = "MOCK-PROJECT-KEY";        // gitleaks:allow
    public const string OAuthToken = "MOCK-OAUTH-ACCESS-TOKEN"; // gitleaks:allow

    private WebApplication? _routeUpstream;
    private readonly List<FakeMcpServer> _mcpServers = [];

    public string CertDirectory { get; } =
        Path.Combine(Path.GetTempPath(), $"ravensport-approval-{Guid.NewGuid():n}");

    internal SystemTestHost? Host { get; set; }

    internal SystemTestEnvironment.Account? Account { get; set; }

    /// <summary>The token the mock authorization server issued, once stage 2 has one.</summary>
    public string? IssuedToken { get; set; }

    /// <summary>Where the exported client certificate was written, once stage 4 has run.</summary>
    public string? PfxPath { get; set; }

    /// <summary>The MCP server sitting behind an OAuth-carrying route.</summary>
    internal FakeMcpServer? SecuredMcpServer { get; set; }

    private string? _abortReason;

    /// <summary>Records the first failure, so later stages can say what they are waiting on.</summary>
    public void Abort(string stage, Exception ex) =>
        _abortReason ??= $"{stage} failed: {ex.GetType().Name}: {ex.Message}";

    /// <summary>
    /// Throws when an earlier stage failed, naming it. Called at the top of every stage after the
    /// first, because a stage that runs on wreckage reports a second failure that is not its own.
    /// </summary>
    public void RequireEarlierStagesPassed()
    {
        if (_abortReason is not null)
        {
            throw new InvalidOperationException($"Skipped — an earlier stage did not pass. {_abortReason}");
        }
    }

    internal SystemTestHost RequireHost()
    {
        RequireEarlierStagesPassed();
        return Host ?? throw new InvalidOperationException("Skipped — the host was never started.");
    }

    /// <summary>What the route upstream echoes back: everything it was sent.</summary>
    public string RouteUpstreamUrl => _routeUpstream!.Services
        .GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

    internal async Task<FakeMcpServer> StartMcpServerAsync()
    {
        var server = await FakeMcpServer.StartAsync();
        _mcpServers.Add(server);
        return server;
    }

    public async Task InitializeAsync()
    {
        // Deliberately nothing that touches a vault. This runs even when the suite is about to skip
        // for want of a token, so it has to be free of anything that needs one.
        Directory.CreateDirectory(CertDirectory);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _routeUpstream = builder.Build();
        _routeUpstream.Run(async context =>
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            var headers = context.Request.Headers
                .ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { headers, body }));
        });
        await _routeUpstream.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Host is not null) await Host.DisposeAsync();
        foreach (var server in _mcpServers) await server.DisposeAsync();

        if (_routeUpstream is not null)
        {
            await _routeUpstream.StopAsync();
            await _routeUpstream.DisposeAsync();
        }

        try { Directory.Delete(CertDirectory, recursive: true); } catch { /* best effort */ }
    }

    // ClearStoreAsync used to live here, emptying the store in memory at the top of stage 1. It was
    // removed rather than left unused: the opening sweep now rewrites the note to index nothing, so
    // the store loads empty on its own, and calling this afterwards spent a vault write restating
    // that. This suite's ceiling is 1Password's hundred writes an hour, so a write that asserts
    // something already true is one the next run does not get to make.
}
