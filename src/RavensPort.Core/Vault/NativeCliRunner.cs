using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

public sealed class NativeCliRunner : ICliRunner
{

    private static bool _initialized = false;
    private static readonly object _initLock = new();

    /// <summary>
    /// One call into the DLL at a time. <c>InitializeOP</c> leaves a client in a package-level
    /// variable on the Go side, so every exported function reads shared state — and the callers
    /// here do fan out: <c>FindAdoptableVaultsAsync</c> lists every candidate vault concurrently.
    /// That was harmless only because the calls used to run inline on one thread; moving them to
    /// the pool would make the concurrency real, so this puts back the serialisation that was
    /// previously an accident of the threading.
    /// </summary>
    private static readonly SemaphoreSlim _callGate = new(1, 1);

    /// <summary>
    /// The pipe 1Password publishes for desktop app integration.
    ///
    /// Checked directly rather than inferred from a failed call, because of a 1Password defect that
    /// makes merely *loading* its library destructive — see <see cref="RequireIntegrationChannel"/>.
    /// </summary>
    private const string IntegrationChannel = @"\\.\pipe\1password-sdk-integrations";

    /// <summary>
    /// The flag every item verb carries. Named because the dispatcher below reads it out of the
    /// argument list five times, and a typo in one of those would silently address the default
    /// vault instead of the configured one.
    /// </summary>
    private const string VaultFlag = "--vault";

    private readonly ActivityLog? _activityLog;
    private readonly IOnePasswordNativeClient _client;
    private readonly Func<bool> _integrationChannelPresent;
    private readonly OnePasswordSession? _session;

    /// <param name="integrationChannelPresent">
    /// Whether 1Password is listening. A seam for tests, which must not depend on whether the
    /// machine running them happens to have 1Password open.
    /// </param>
    /// <param name="session">
    /// The service-account token, when the user has chosen that way in. Its presence selects the
    /// whole authentication mode — see <see cref="UsesServiceAccount"/>.
    /// </param>
    public NativeCliRunner(
        ActivityLog? activityLog = null,
        IOnePasswordNativeClient? client = null,
        Func<bool>? integrationChannelPresent = null,
        OnePasswordSession? session = null)
    {
        _activityLog = activityLog;
        _client = client ?? new OnePasswordNativeClientWrapper();
        _integrationChannelPresent = integrationChannelPresent ?? (() => File.Exists(IntegrationChannel));
        _session = session;
    }

    /// <summary>
    /// Whether this runner is authenticating with a service-account token rather than through the
    /// desktop app. The token's presence is the whole switch: the SDK refuses to accept both, and a
    /// user who has pasted one has said which they want.
    /// </summary>
    private bool UsesServiceAccount => _session?.HasToken == true;

    /// <summary>
    /// Refuses to go near the SDK unless 1Password is actually listening.
    ///
    /// This exists because of a 1Password defect, and the cost of tripping it is out of all
    /// proportion to the mistake. <b>If any process has 1Password's <c>op_sdk_ipc_client.dll</c>
    /// mapped when 1Password starts, 1Password does not create the integration pipe at all</b> — a
    /// mapped DLL cannot be replaced or deleted on Windows, and its startup evidently touches that
    /// file. It never retries, so the channel stays missing for the entire life of that 1Password
    /// process, and releasing the library afterwards does not bring it back.
    ///
    /// Reproduced in isolation with a process that did nothing but <c>LoadLibrary</c> — never
    /// connected, never authenticated: 1Password restarted with it running, no pipe; restarted
    /// without it, pipe present.
    ///
    /// So the damage is done by loading the library at the wrong moment, and the only defence is
    /// not to load it then. RavensPort cannot know when the user will start 1Password, but it does
    /// know 1Password can only start while it is not running — so declining to load whenever the
    /// channel is absent keeps the file free across every window in which a start could happen.
    /// Without this, an install set to run at login held the library from boot and 1Password could
    /// never open the channel again, on any restart, forever.
    ///
    /// The library cannot be released once loaded — the SDK caches it in a package-level variable
    /// in an internal package — which is why the message differs once that has happened. There the
    /// only honest advice is to restart RavensPort, because this process is now the thing standing
    /// in 1Password's way.
    /// </summary>
    private void RequireIntegrationChannel()
    {
        // A service account never goes near any of this. The SDK routes a token to its own embedded
        // core and reaches 1Password over the network, so no pipe is opened, no library of theirs is
        // mapped, and nothing this guard protects against can happen. Applying it anyway would block
        // the one authentication mode that is immune to the fault it exists for -- and block it on
        // exactly the machines the token mode is for, where 1Password is not installed at all.
        if (UsesServiceAccount) return;

        if (_integrationChannelPresent()) return;

        throw new VaultCliException(_initialized
            ? "1Password is not running, and RavensPort has already loaded 1Password's integration "
              + "library — which stops 1Password from opening its integration channel when it "
              + "starts again. Start 1Password, then restart RavensPort."
            : "1Password is not running. Start the 1Password desktop app and try again. If you have "
              + "just switched on Settings > Developer > \"Integrate with other apps\", restart "
              + "1Password too — it only opens the integration channel when it starts.");
    }

    /// <summary>
    /// When a reconnect was last attempted, and how long before another is allowed.
    ///
    /// A reconnect is not free. Against a running-but-locked 1Password it raises the authorization
    /// prompt, and the calls that reach this runner are not paced by the sync queue's backoff —
    /// loading the store fans out over items, and a probe walks the vault list. An activity log
    /// showed six failures inside four seconds, which without this would be six prompts.
    ///
    /// Only failing reconnects are throttled: a successful one clears the stamp, so the cooldown
    /// can never delay a connection that is actually working.
    /// </summary>
    private static DateTimeOffset _lastReconnectAttempt = DateTimeOffset.MinValue;

    private static readonly TimeSpan ReconnectCooldown = TimeSpan.FromSeconds(10);

    public static void ResetInitialization()
    {
        lock (_initLock)
        {
            MarkNotInitialized();

            // The user is asking, so nothing owed from earlier failures should stand in the way.
            ClearReconnectCooldown();
        }
    }

    /// <summary>
    /// The only places the process-wide connection flags are written.
    ///
    /// Static because the flags are, and the flags are static because the connection is: the Go
    /// side keeps one package-level client, so "connected" and "last tried" are facts about the
    /// process rather than about any one runner. Instance methods below reach the state through
    /// these rather than assigning it directly, which is what keeps every writer visible from one
    /// place. Callers of the first two hold <see cref="_initLock"/>.
    /// </summary>
    private static void MarkInitialized() => _initialized = true;

    /// <inheritdoc cref="MarkInitialized"/>
    private static void MarkNotInitialized() => _initialized = false;

    /// <inheritdoc cref="MarkInitialized"/>
    private static void ClearReconnectCooldown() => _lastReconnectAttempt = DateTimeOffset.MinValue;

    /// <summary>
    /// Claims the right to rebuild the connection, or reports that one was tried too recently.
    /// </summary>
    private static bool TryBeginReconnect()
    {
        lock (_initLock)
        {
            var now = DateTimeOffset.UtcNow;
            if (now - _lastReconnectAttempt < ReconnectCooldown) return false;

            _lastReconnectAttempt = now;
            return true;
        }
    }

    /// <summary>
    /// Whether a failure is a stale connection worth rebuilding — see
    /// <see cref="VaultAuthorization.ClientIsDead"/>.
    ///
    /// A decline is excluded even if it arrives wearing the same message. Rebuilding the connection
    /// is itself a prompt, so doing it in answer to "no" is the app asking again immediately.
    /// </summary>
    private static bool ShouldReconnect(string? message) =>
        VaultAuthorization.ClientIsDead(message) && !VaultAuthorization.WasDeclined(message);

    private void EnsureInitialized()
    {
        if (_initialized) return;

        lock (_initLock)
        {
            if (_initialized) return;

            // A service account authenticates with the token alone. Demanding an account name here
            // would be asking for something the SDK will not accept alongside it -- and something a
            // headless machine has no way to know, since the name is read off the desktop app's
            // sidebar and that app is precisely what this mode does without.
            if (UsesServiceAccount)
            {
                InitializeServiceAccount();
                return;
            }

            var accountName = LocalSettings.Current.OnePasswordAccountName;
            if (string.IsNullOrWhiteSpace(accountName))
            {
                accountName = Environment.GetEnvironmentVariable("OP_ACCOUNT");
            }
            if (string.IsNullOrWhiteSpace(accountName))
            {
                throw new VaultCliException("1Password SDK requires your Account Name (as shown in the top-left sidebar of the 1Password app) to connect. Please configure it in the Setup page.");
            }

            try
            {
                _client.Initialize(accountName);
                MarkInitialized();
            }
            catch (Exception ex)
            {
                // Left false on purpose — it already is, and it matters. Every later call retries
                // this, which is how the app heals itself once 1Password is running again: nothing
                // has to notice the restart, the next attempt simply connects.
                throw new VaultCliException(
                    $"Failed to connect to 1Password Desktop App for account '{accountName}': "
                    + Explain(ex.Message), ex);
            }
        }
    }

    /// <summary>
    /// Connects with the service-account token. Caller holds <see cref="_initLock"/>.
    ///
    /// The token is read from the session and handed straight across; it is never copied into a
    /// field here, never logged, and never named in the failure message. What comes back from the
    /// SDK on a bad token says the token was refused, not what it was.
    /// </summary>
    private void InitializeServiceAccount()
    {
        try
        {
            _client.InitializeServiceAccount(_session!.CurrentToken!);
            MarkInitialized();
        }
        catch (Exception ex)
        {
            // Left false, like the desktop path: a token rejected because the machine was offline
            // should start working again when it is not, without anything having to notice.
            throw new VaultCliException(
                "Could not connect to 1Password with the service account token. Check that the "
                + "token is current and that the account has been granted access to the vault. "
                + $"({ex.Message})", ex);
        }
    }

    /// <summary>
    /// Puts a usable sentence in front of the SDK's own wording when that wording is a bare Win32
    /// error — see <see cref="VaultAuthorization.IsUnreachable"/> for what it actually means.
    ///
    /// The restart clause is the whole point of writing this. "1Password isn't running" is easy
    /// enough to work out; that turning the integration on inside a running 1Password does nothing
    /// until it is restarted is not, and it is the failure people actually hit. Without it the log
    /// reads as though RavensPort has misplaced a file of its own.
    /// </summary>
    private static string Explain(string message) =>
        VaultAuthorization.IsUnreachable(message)
            ? "1Password is not reachable. Start the 1Password desktop app — and if you have just "
              + "turned on Settings > Developer > \"Integrate with other apps\", restart 1Password, "
              + "because it only opens the integration channel when it starts. "
              + $"({message})"
            : message;

    /// <summary>
    /// Throws away the current SDK client and connects again. Throws if 1Password will not have it —
    /// still locked, or the authorization prompt declined again — which is the honest answer and
    /// leaves the change pending rather than reporting a save that did not happen.
    /// </summary>
    private void Reinitialize()
    {
        lock (_initLock)
        {
            MarkNotInitialized();
        }

        EnsureInitialized();
    }

    /// <summary>
    /// Every call here is a blocking P/Invoke into onepassword.dll, so unlike <see cref="CliRunner"/>
    /// — which awaits real process I/O — there is nothing in the body that yields. Returning
    /// <c>Task.FromResult</c> from a synchronous body meant the work ran inline on whichever thread
    /// awaited it, and for anything driven by a button that thread is the WPF dispatcher: Disconnect
    /// flushes pending changes before it asks for confirmation, so the window froze for as long as
    /// the 1Password SDK took to write every changed item. Proton Pass never showed it because its
    /// runner yields at the first await.
    ///
    /// <c>Task.Run</c> rather than making the DLL calls asynchronous, because they cannot be: the
    /// exported functions are synchronous and there is no overlapped variant to await. The blocking
    /// has to happen on some thread, and a pool thread is the right one.
    ///
    /// <paramref name="timeout"/> is still not honoured. A P/Invoke in progress cannot be abandoned,
    /// and returning early while the DLL keeps writing would be worse than waiting — the caller
    /// would report a failure for a save that is about to succeed. The SDK's own timeouts apply.
    /// </summary>
    public async Task<CliResult> RunAsync(
        string exePath,
        IReadOnlyList<string> args,
        string? stdin = null,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        await _callGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // ConfigureAwait(false) all the way out, so the continuation does not queue back onto
            // the dispatcher — that would put the JSON parsing this returns into back on the UI
            // thread for every vault read.
            return await Task.Run(() => Execute(args, stdin), ct).ConfigureAwait(false);
        }
        finally
        {
            _callGate.Release();
        }
    }

    private CliResult Execute(IReadOnlyList<string> args, string? stdin)
    {
        // Answered before EnsureInitialized, and that ordering is the whole point.
        //
        // InitializeOP connects to the 1Password desktop app, which is what raises the unlock
        // prompt — so anything that runs it is an interruption to the user, whether or not it
        // needed the connection. --version needs nothing: the answer below is a constant, because
        // there is no CLI here to ask. Initialising first meant merely *looking* for 1Password
        // demanded that the user unlock it, which is exactly the prompt the setup page's discovery
        // probe exists to avoid — see VaultProbeDepth.
        if (args.Contains("--version"))
        {
            return new CliResult(0, "0.4.1", "");
        }

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // Ahead of EnsureInitialized, because the whole point is to not load the library. After
            // the --version answer above, because that must stay free of 1Password entirely.
            RequireIntegrationChannel();

            EnsureInitialized();

            CliResult result;
            try
            {
                result = Dispatch(args, stdin);
            }
            catch (VaultCliException ex) when (ShouldReconnect(ex.Message))
            {
                // Rethrown rather than reconnected when one was tried moments ago: the answer would
                // be the same, and against a locked 1Password the asking is the cost. The failure
                // still reaches the caller, which leaves the change pending as it should.
                if (!TryBeginReconnect()) throw;

                // The connection died between calls — see VaultAuthorization.ClientIsDead. Rebuilding
                // it is the only way back, and it is safe to repeat the call: every operation here is
                // either a read or an idempotent write against an item id that does not change, and
                // the one that threw never reached 1Password at all.
                //
                // Once, not on a loop: a second failure is returned as the failure it is, so a user
                // who is deliberately leaving 1Password locked is asked at most once per attempt.
                _activityLog?.Log(
                    "VAULT the connection to 1Password was no longer usable — rebuilding it and "
                    + "trying the same operation again");

                Reinitialize();
                result = Dispatch(args, stdin);
            }

            // Here rather than after a successful connect, and the difference is not academic: a
            // rebuild can hand back a client that connects and still cannot talk to 1Password, and
            // clearing the cooldown on that put the burst straight back — one rebuild per call, the
            // prompts this exists to prevent. Reaching this line means the connection actually
            // carried an operation, which is the only evidence worth trusting.
            ClearReconnectCooldown();

            Log(args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (VaultCliException ex)
        {
            var result = new CliResult(1, "", ex.Message);
            Log(args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            var result = new CliResult(1, "", ex.ToString());
            Log(args, result, stopwatch.ElapsedMilliseconds);
            return result;
        }
    }

    /// <summary>
    /// Turns one <c>op</c> command line into the matching SDK call. Separate from
    /// <see cref="Execute"/> so a dead client can be reconnected and the identical call made again
    /// without duplicating the dispatch.
    /// </summary>
    private CliResult Dispatch(IReadOnlyList<string> args, string? stdin)
    {
        var result = Route(args, stdin);

        // A dead client can also arrive as a failed result rather than an exception, depending
        // on which call reported it. Raised so the one recovery path in Execute covers both.
        if (!result.Succeeded && ShouldReconnect(result.StdErr)) throw new VaultCliException(result.StdErr);

        return result;
    }

    /// <summary>
    /// The command line, matched verb by verb. Every arm reads the same way on purpose: the vault
    /// comes from the --vault flag when there is one, the item id is the third word when there is
    /// one, and anything unrecognised is an unsupported command rather than a guess.
    /// </summary>
    private CliResult Route(IReadOnlyList<string> args, string? stdin) => args switch
    {
        ["vault", "list", ..] => Json(_client.ListVaults(), "[]"),

        ["vault", "create", var name, ..] =>
            Json(_client.CreateVault(name, Flag(args, "--description") ?? ""), "{}"),

        ["item", "list", ..] => Json(_client.ListItems(Flag(args, VaultFlag) ?? ""), "[]"),

        ["item", "get", var itemId, ..] => GetItem(Flag(args, VaultFlag) ?? "", itemId),

        ["item", "create", ..] =>
            Json(_client.CreateItem(Flag(args, VaultFlag) ?? "", stdin ?? ""), "{}"),

        ["item", "edit", var itemId, ..] =>
            Json(_client.EditItem(Flag(args, VaultFlag) ?? "", itemId, stdin ?? ""), "{}"),

        ["item", "delete", var itemId, ..] => DeleteItem(Flag(args, VaultFlag) ?? "", itemId),

        _ => new CliResult(1, "", "Command not supported by NativeCliRunner: " + string.Join(" ", args)),
    };

    /// <summary>The value after a flag, or null when the flag is absent or ends the line.</summary>
    private static string? Flag(IReadOnlyList<string> args, string name)
    {
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] == name) return args[i + 1];
        }

        return null;
    }

    /// <summary>A successful result carrying the SDK's JSON, or the empty document it stands for.</summary>
    private static CliResult Json(JsonNode? node, string whenAbsent) =>
        new(0, node?.ToJsonString() ?? whenAbsent, "");

    /// <summary>
    /// A missing item is reported the way `op` reports it, because
    /// <see cref="OnePasswordVaultProvider"/> reads that wording to tell "gone" from "could not
    /// look" — and treats everything it does not recognise as the latter.
    /// </summary>
    private CliResult GetItem(string vaultId, string itemId) =>
        _client.GetItem(vaultId, itemId) is { } item
            ? new CliResult(0, item.ToJsonString(), "")
            : new CliResult(1, "", "isn't an item");

    private CliResult DeleteItem(string vaultId, string itemId)
    {
        _client.DeleteItem(vaultId, itemId);
        return new CliResult(0, "", "");
    }

    private void Log(IReadOnlyList<string> args, CliResult result, long elapsedMs)
    {
        var message = $"VAULT op {string.Join(' ', args).TrimEnd()} -> exit {result.ExitCode} in {elapsedMs}ms";

        if (!result.Succeeded && result.FirstErrorLine() is { Length: > 0 } error)
        {
            message += $" ({error})";
        }

        _activityLog?.Log(message);
    }

    public Task<CliResult> RunStreamingAsync(
        string exePath,
        IReadOnlyList<string> args,
        Action<string> onOutputLine,
        IReadOnlyDictionary<string, string>? env = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        throw new NotImplementedException("Streaming not needed for NativeCliRunner");
    }
}
