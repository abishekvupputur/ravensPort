using CommunityToolkit.Mvvm.ComponentModel;

namespace RavensPort.UI.ViewModels;

/// <summary>
/// Which of the two things the window is showing: the setup page, or the tabs.
///
/// The app cannot do anything at all without a reachable password manager — there is no local
/// configuration to fall back on — so "not set up" is a whole-window state rather than a banner
/// over a UI whose every control would fail.
/// </summary>
public sealed partial class MainWindowViewModel(VaultStatusViewModel vaultStatus) : ObservableObject
{
    /// <summary>Drives the banner above the tabs. Exposed here so the whole shell binds to one context.</summary>
    public VaultStatusViewModel VaultStatus { get; } = vaultStatus;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNormal))]
    private bool _isGated = true;

    /// <summary>True once the vault is reachable and the proxy is running.</summary>
    public bool IsNormal => !IsGated;

    public void EnterNormalMode() => IsGated = false;

    public void EnterSetupMode() => IsGated = true;
}
