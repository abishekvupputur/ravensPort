using RavensPort.Core.Diagnostics;

namespace RavensPort.Core.Vault;

/// <summary>What the app knows about both backends right now.</summary>
/// <param name="Statuses">One entry per supported manager, in a stable order for the setup page.</param>
/// <param name="Selected">The chosen backend, once there is one.</param>
/// <param name="NeedsAChoice">
/// True when more than one manager qualifies and nothing in the vaults says which was meant. The
/// backend choice is the one piece of state that cannot live in the vault, and it is deliberately
/// not stored anywhere else — so this asks, every launch, rather than remembering.
/// </param>
public sealed record VaultGateStatus(
    IReadOnlyList<VaultStatus> Statuses,
    VaultBackendKind Selected,
    bool NeedsAChoice)
{
    public bool IsReady => Selected != VaultBackendKind.None && !NeedsAChoice;

    public VaultStatus? For(VaultBackendKind kind) => Statuses.FirstOrDefault(s => s.Kind == kind);
}

/// <summary>
/// Decides which password manager backs the store, and whether the app can start at all.
///
/// Resolution is by discovery rather than by remembered preference: whichever manager's
/// RavensPort vault already holds a RavensPort configuration <em>is</em> the backend. That answers
/// the normal case with no stored state, which matters because there is nowhere to store it — the
/// whole design is that nothing about this app persists outside the vault. Only a genuine tie asks.
/// </summary>
public sealed class VaultGateService
{
    private readonly OnePasswordVaultProvider _onePassword;
    private readonly ProtonPassVaultProvider _protonPass;
    private readonly ActivityLog _activityLog;

    public VaultGateService(
        OnePasswordVaultProvider onePassword,
        ProtonPassVaultProvider protonPass,
        ActivityLog activityLog)
    {
        _onePassword = onePassword;
        _protonPass = protonPass;
        _activityLog = activityLog;

        Status = new VaultGateStatus([], VaultBackendKind.None, NeedsAChoice: false);
    }

    /// <summary>
    /// Set by <see cref="Disconnect"/> until the user picks a manager again.
    ///
    /// Without it, disconnecting would be undone by the very next probe: a single ready manager
    /// resolves itself with no question asked, which is right at startup and wrong immediately
    /// after someone has said they want to stop using it.
    /// </summary>
    private bool _disconnected;

    /// <summary>
    /// The store for a single-use session, while one is running. Held here rather than merely
    /// assigned to <see cref="Selected"/> so that <see cref="Disconnect"/> can drop the instance
    /// outright: the configuration only ever existed inside it, so letting go of it is the purge.
    /// </summary>
    private InMemoryVault? _singleUse;

    public VaultGateStatus Status { get; private set; }

    /// <summary>True after a disconnect, until a backend is chosen again.</summary>
    public bool IsDisconnected => _disconnected;

    /// <summary>True while the app is running on memory alone — see <see cref="UseSingleUse"/>.</summary>
    public bool IsSingleUse => _singleUse is not null;

    /// <summary>The active backend. Never null so callers do not have to special-case startup.</summary>
    public IConfigVault Selected { get; private set; } = new InMemoryVault();

    public event Action<VaultGateStatus>? StatusChanged;

    /// <summary>
    /// Probes both managers and resolves a backend if it can. Safe to call repeatedly — the setup
    /// page's "Check again" is exactly this.
    /// </summary>
    /// <param name="depth">
    /// How much the probe may disturb the user. <see cref="VaultProbeDepth.Discovery"/> — what the
    /// setup page's startup check uses — cannot resolve a backend at all, because knowing whether a
    /// manager is signed in means asking it, and asking it is what raises the prompt. It answers
    /// "what is installed here", and the user connects the one they meant. The default stays
    /// <see cref="VaultProbeDepth.Full"/> for callers that have just done something authenticating
    /// and need the real answer, such as a Proton Pass sign-in.
    /// </param>
    public async Task<VaultGateStatus> EvaluateAsync(
        VaultProbeDepth depth = VaultProbeDepth.Full, CancellationToken ct = default)
    {
        // Concurrently: each is a subprocess launch that may sit on an unlock prompt, and running
        // them in sequence would double the worst case on the startup path.
        //
        // The one place Proton Pass is removed from the store build, and deliberately the only one.
        // Everything downstream — the setup page's cards, the tie-break, the Settings tab's sign-out
        // button — is driven by what this returns, so dropping the probe here takes the whole
        // feature out of the UI without a second switch anywhere else to fall out of step with it.
        // See BuildProfile.
        var probes = BuildProfile.ProtonPassEnabled
            ? await Task.WhenAll(
                ProbeSafelyAsync(_onePassword, depth, ct),
                ProbeSafelyAsync(_protonPass, depth, ct))
            : await Task.WhenAll(
                ProbeSafelyAsync(_onePassword, depth, ct));

        // A single-use session outranks whatever the probes found: the user chose memory, and a
        // manager quietly becoming available is not a reason to move their configuration into it.
        // The probes still run, so the setup page shows the truth if they disconnect back to it.
        if (_singleUse is not null)
        {
            return Publish(new VaultGateStatus(probes, VaultBackendKind.SingleUse, NeedsAChoice: false));
        }

        var ready = probes.Where(p => p.IsReady).ToList();

        // Disconnected means the answer to "which backend" is nobody, no matter what the probes
        // found. NeedsAChoice so the setup page offers the ready ones as buttons to connect back.
        if (_disconnected)
        {
            return Publish(new VaultGateStatus(probes, VaultBackendKind.None, NeedsAChoice: ready.Count > 0));
        }

        return ready.Count switch
        {
            0 => Publish(new VaultGateStatus(probes, VaultBackendKind.None, NeedsAChoice: false)),
            1 => Publish(new VaultGateStatus(probes, ready[0].Kind, NeedsAChoice: false), ready[0].Kind),
            _ => await ResolveTieAsync(probes, ready, ct),
        };
    }

    /// <summary>
    /// Both managers hold the vault. Whichever one already has a configuration in it is the one
    /// that was being used; only if that is ambiguous does the user get asked.
    /// </summary>
    private async Task<VaultGateStatus> ResolveTieAsync(
        VaultStatus[] probes, List<VaultStatus> ready, CancellationToken ct)
    {
        // Reading a configuration can mean several CLI round trips. These are independent vaults,
        // so doing them one after the other made the two-manager setup path take the sum of both
        // waits. Start both reads together and wait only for the slower one.
        var configurationChecks = ready.Select(async status => new
        {
            status.Kind,
            HasConfiguration = await HasConfigurationAsync(ProviderFor(status.Kind), ct),
        });
        var configured = (await Task.WhenAll(configurationChecks))
            .Where(check => check.HasConfiguration)
            .Select(check => check.Kind)
            .ToList();

        if (configured.Count == 1)
        {
            _activityLog.Log($"STARTUP both password managers are available; using "
                             + $"{VaultLockGuidance.DisplayName(configured[0])}, which holds the configuration");

            return Publish(new VaultGateStatus(probes, configured[0], NeedsAChoice: false), configured[0]);
        }

        // Either both hold a configuration or neither does. Guessing would mean silently reading
        // one and silently overwriting the other, so this is a question only the user can answer.
        return Publish(new VaultGateStatus(probes, VaultBackendKind.None, NeedsAChoice: true));
    }

    /// <summary>
    /// Asks one manager, in full, because the user has just pressed a button that says so.
    ///
    /// This is the whole of the deferred-authentication design. Startup discovers what is installed
    /// and stops; the unlock prompt, the desktop-app approval, the Hello gesture all happen here,
    /// for the one manager named, at the moment the user asked for it. Probing the other one as
    /// well would put back exactly the second prompt this exists to remove.
    /// </summary>
    public async Task<VaultGateStatus> ConnectAsync(VaultBackendKind kind, CancellationToken ct = default)
    {
        EnsureBackendIsInThisBuild(kind);

        // The attempt is the un-disconnecting, whether or not it succeeds. Leaving the flag set
        // would have the next evaluation report the user as still disconnected while they are
        // plainly in the middle of connecting.
        _disconnected = false;
        _singleUse = null;

        // Undoes the discovery half of Forget before probing. Pressing Connect *is* the user naming
        // this manager again, so rediscovering the vault they left is what they asked for — the ban
        // exists to stop an automatic probe reattaching, and this is not automatic.
        AllowDiscovery(kind);

        var probe = await ProbeSafelyAsync(ProviderFor(kind), VaultProbeDepth.Full, ct);

        // Writing is re-opened only now, and only because the probe resolved a vault. Connecting
        // was previously a third way to become the selected backend, alongside creating a vault and
        // naming one — and the only one that never cleared this. The result was an install that
        // read its configuration perfectly, reported itself connected, and refused every save with
        // "RavensPort is not connected to a 1Password vault. Choose a vault on the setup page
        // first", pointing the user at a page they had just successfully used.
        if (probe.IsReady) AllowWrites(kind);

        // Only this manager's entry moves. The other card keeps whatever the last probe said about
        // it — which is the honest answer, since nothing has asked it anything since.
        List<VaultStatus> statuses = Status.Statuses.Any(s => s.Kind == kind)
            ? [.. Status.Statuses.Select(s => s.Kind == kind ? probe : s)]
            : [.. Status.Statuses, probe];

        // Ready means this one is the backend, with no tie to break: the user named it. Anything
        // else leaves the selection alone and lets the card explain what is still missing.
        return probe.IsReady
            ? Publish(new VaultGateStatus(statuses, kind, NeedsAChoice: false), kind)
            : Publish(new VaultGateStatus(statuses, Status.Selected, NeedsAChoice: false));
    }

    /// <summary>
    /// Runs on memory alone for this session — full functionality, no password manager, nothing
    /// written anywhere.
    ///
    /// For trying the app out, and for testing it, without first handing it a vault. The trade is
    /// stated rather than hidden: the configuration lives in one object in this process, so it goes
    /// when the session does — <see cref="Disconnect"/>, or exit.
    /// </summary>
    public VaultGateStatus UseSingleUse()
    {
        _disconnected = false;

        // A fresh instance every time, so an earlier single-use session cannot leak into a later
        // one through a store that was never emptied.
        _singleUse = InMemoryVault.ForSingleUse();
        Selected = _singleUse;

        _activityLog.Log(
            "STARTUP single use — RavensPort is running without a password manager, and this "
            + "configuration is held in memory only");

        return Publish(new VaultGateStatus(Status.Statuses, VaultBackendKind.SingleUse, NeedsAChoice: false));
    }

    /// <summary>Records the user's answer for this run. Deliberately not persisted anywhere.</summary>
    public VaultGateStatus SelectBackend(VaultBackendKind kind)
    {
        EnsureBackendIsInThisBuild(kind);

        _disconnected = false;
        _singleUse = null;

        var status = Status with { Selected = kind, NeedsAChoice = false };
        return Publish(status, kind);
    }

    /// <summary>
    /// Refuses a backend this build does not ship. Belt to the probe's braces: the store build
    /// never probes Proton Pass, so no card offers it and nothing should ever reach here with it —
    /// but every one of these entry points is public, and a selection that slipped through would
    /// hand the app a provider whose CLI it must not go looking for. Throwing says which build the
    /// user is on, which is the one thing a "nothing happens" would not.
    /// </summary>
    private static void EnsureBackendIsInThisBuild(VaultBackendKind kind)
    {
        if (kind == VaultBackendKind.ProtonPass && !BuildProfile.ProtonPassEnabled)
        {
            throw new NotSupportedException(
                "Proton Pass is not available in the Microsoft Store build of RavensPort. Use "
                + "1Password, or single use, or install the version from the RavensPort releases page.");
        }
    }

    /// <summary>Creates a vault with the user's chosen name in the given manager, then re-evaluates.</summary>
    public async Task<VaultGateStatus> CreateVaultAsync(
        VaultBackendKind kind, string vaultName, CancellationToken ct = default)
    {
        EnsureBackendIsInThisBuild(kind);

        _disconnected = false;

        await ProviderFor(kind).CreateVaultAsync(vaultName, ct);
        _activityLog.Log($"STARTUP created the '{ProviderFor(kind).VaultName}' vault in {VaultLockGuidance.DisplayName(kind)}");

        return await ResolveAfterUserChoiceAsync(kind, ct);
    }

    /// <summary>
    /// Points a manager at a vault the user already has, then re-evaluates. Throws
    /// <see cref="VaultAdoptionException"/> when that vault may not be used.
    /// </summary>
    public async Task<VaultGateStatus> UseExistingVaultAsync(
        VaultBackendKind kind, string vaultName, CancellationToken ct = default)
    {
        EnsureBackendIsInThisBuild(kind);

        _disconnected = false;

        await ProviderFor(kind).UseExistingVaultAsync(vaultName, ct);

        return await ResolveAfterUserChoiceAsync(kind, ct);
    }

    /// <summary>
    /// Stops using the password manager: both providers forget the vault they resolved, and the
    /// app is back where it starts, holding nothing.
    ///
    /// The caller is expected to clear the in-memory store as well. Leaving it loaded would be the
    /// worst of both worlds — the proxy would still be spending the user's tokens using a
    /// configuration they have just disconnected from, and with nowhere to save changes to.
    /// </summary>
    public VaultGateStatus Disconnect()
    {
        _onePassword.Forget();
        _protonPass.Forget();

        var wasSingleUse = _singleUse is not null;

        _disconnected = true;

        // The single-use store is dropped, not emptied. It was the only copy of that configuration
        // — there is no vault behind it — so releasing the object is what makes the purge complete
        // rather than a matter of trusting a Clear() to have reached everything.
        _singleUse = null;
        Selected = new InMemoryVault();

        _activityLog.Log(wasSingleUse
            ? "VAULT left single use — the configuration held in memory has been discarded"
            : "VAULT disconnected from the password manager — RavensPort is holding no configuration");

        return Publish(new VaultGateStatus([], VaultBackendKind.None, NeedsAChoice: false));
    }

    /// <summary>
    /// Keeps the backend the user just created or named. The operation above has already proved
    /// that this provider can open the vault; probing both managers again would repeat the costly
    /// tie-break reads and can only replace the user's explicit choice with the other manager.
    /// </summary>
    private Task<VaultGateStatus> ResolveAfterUserChoiceAsync(VaultBackendKind kind, CancellationToken ct) =>
        Task.FromResult(SelectBackend(kind));

    /// <summary>
    /// The two halves of undoing <see cref="IConfigVault.Forget"/>, kept off the interface: only a
    /// real password-manager backend can be disconnected in the first place, so neither the
    /// in-memory store nor the gated forwarder has anything to undo.
    /// </summary>
    private void AllowDiscovery(VaultBackendKind kind)
    {
        switch (kind)
        {
            case VaultBackendKind.OnePassword: _onePassword.AllowDiscovery(); break;
            case VaultBackendKind.ProtonPass: _protonPass.AllowDiscovery(); break;
        }
    }

    private void AllowWrites(VaultBackendKind kind)
    {
        switch (kind)
        {
            case VaultBackendKind.OnePassword: _onePassword.AllowWrites(); break;
            case VaultBackendKind.ProtonPass: _protonPass.AllowWrites(); break;
        }
    }

    public IConfigVault ProviderFor(VaultBackendKind kind) => kind switch
    {
        VaultBackendKind.OnePassword => _onePassword,
        VaultBackendKind.ProtonPass => _protonPass,
        VaultBackendKind.SingleUse when _singleUse is not null => _singleUse,
        _ => Selected,
    };

    /// <summary>
    /// Whether this manager's vault already holds a RavensPort configuration. A load rather than a
    /// guess, because "there is a vault" and "there is anything in it" are different questions and
    /// only the second one identifies which manager was being used.
    /// </summary>
    private async Task<bool> HasConfigurationAsync(IConfigVault vault, CancellationToken ct)
    {
        try
        {
            var store = await vault.LoadAsync(ct);

            return store.Credentials.Count > 0
                   || store.Routes.Count > 0
                   || store.Upstreams.Count > 0
                   || store.McpFunnels.Count > 0;
        }
        catch (Exception ex) when (ex is VaultCliException or VaultLockedException)
        {
            // Unreadable is not the same as empty, but for choosing between two managers it leads
            // to the same place: this one cannot be shown to hold the configuration.
            _activityLog.Log($"STARTUP could not read {VaultLockGuidance.DisplayName(vault.Kind)}: {ex.Message}");
            return false;
        }
    }

    private static async Task<VaultStatus> ProbeSafelyAsync(
        IConfigVault vault, VaultProbeDepth depth, CancellationToken ct)
    {
        try
        {
            return await vault.ProbeAsync(depth, ct);
        }
        catch (Exception ex)
        {
            // A probe must never be able to stop the app reaching its setup page — that page is
            // the only thing that can explain what went wrong.
            return VaultStatus.Faulted(vault.Kind, ex.Message);
        }
    }

    private VaultGateStatus Publish(VaultGateStatus status, VaultBackendKind? selected = null)
    {
        Status = status;

        if (selected is { } kind && kind != VaultBackendKind.None) Selected = ProviderFor(kind);

        StatusChanged?.Invoke(status);
        return status;
    }
}
