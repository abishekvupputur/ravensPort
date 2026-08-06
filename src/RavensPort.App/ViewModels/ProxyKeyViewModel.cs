using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RavensPort.App.Services;
using RavensPort.Core.Models;

namespace RavensPort.App.ViewModels;

/// <summary>
/// The key editor shown on a route row and on a funnel row. One view model for both, because the
/// two are the same object with a different owner — a secret, how long it stays valid, and the
/// three things anyone ever does with it (look at it, copy it, replace it).
///
/// Edits are written straight through to the <see cref="ProxyKey"/> the store holds; persisting is
/// the owner's job, via the callback, so a key change is saved by the same path as every other
/// edit on that tab.
/// </summary>
public sealed partial class ProxyKeyViewModel : ObservableObject
{
    /// <summary>What the key belongs to, e.g. "route '/app/gmail'". Used in status messages.</summary>
    private readonly string _owner;

    /// <summary>Raised when the stored key changed, so the owner can persist and report.</summary>
    private readonly Action<string> _onChanged;

    /// <summary>
    /// Raised for something worth saying that changed nothing — copying to the clipboard. Kept
    /// separate so it does not drag a store write (and, on the Routes tab, a YARP rebuild) behind
    /// every click of Copy.
    /// </summary>
    private readonly Action<string> _onStatus;

    private readonly IClipboardService _clipboard;

    /// <summary>Set while the picker is being populated, so loading it is not read as an edit.</summary>
    private bool _loading;

    public ProxyKeyViewModel(
        ProxyKey key,
        string owner,
        Action<string> onChanged,
        Action<string> onStatus,
        IClipboardService clipboard)
    {
        Key = key;
        _owner = owner;
        _onChanged = onChanged;
        _onStatus = onStatus;
        _clipboard = clipboard;

        _loading = true;

        foreach (var option in ProxyKeyLifetime.All) Lifetimes.Add(option);

        // A key whose expiry was set by an older build, by hand, or by a preset that has since
        // changed still has to be selectable in the drop-down — otherwise the picker opens blank
        // and the first interaction silently rewrites an expiry the user never looked at.
        var current = ProxyKeyLifetime.ForKey(key);
        if (!Lifetimes.Contains(current)) Lifetimes.Insert(0, current);

        _selectedLifetime = current;
        _loading = false;
    }

    public ProxyKey Key { get; }

    public ObservableCollection<ProxyKeyLifetime> Lifetimes { get; } = [];

    [ObservableProperty] private bool _isVisible;

    [ObservableProperty] private ProxyKeyLifetime _selectedLifetime;

    /// <summary>Masked unless the user asks to see it, so a shared screen doesn't leak it.</summary>
    public string Display => IsVisible ? Key.Value : new string('•', 32);

    public string ExpirySummary => Key.DescribeExpiry(DateTimeOffset.UtcNow);

    public bool IsExpired => Key.IsExpired(DateTimeOffset.UtcNow);

    public string ToggleLabel => IsVisible ? "Hide" : "Show";

    partial void OnIsVisibleChanged(bool value)
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ToggleLabel));
    }

    partial void OnSelectedLifetimeChanged(ProxyKeyLifetime value)
    {
        if (_loading) return;

        Key.SetLifetime(value.Duration);
        NotifyKeyChanged();

        // Measured from when the key was issued, so a short window on an old key can land in the
        // past. Say what to do about it rather than reporting an expiry date and leaving the
        // endpoint answering 403 with no obvious way back.
        _onChanged(value.Duration switch
        {
            null => $"The proxy key for {_owner} now never expires.",
            _ when IsExpired => $"The proxy key for {_owner} is now past its {value.Label} lifetime, "
                                + "counted from when it was issued — that endpoint answers 403 until "
                                + "you press Regenerate for a fresh key.",
            _ => $"The proxy key for {_owner} {ExpirySummary}.",
        });
    }

    [RelayCommand]
    private void ToggleVisibility() => IsVisible = !IsVisible;

    [RelayCommand]
    private async Task CopyAsync()
    {
        try
        {
            await _clipboard.SetTextAsync(Key.Value);
            _onStatus($"Proxy key for {_owner} copied. Send it as the 'X-Proxy-Key' header, "
                      + "or as '?proxy_key=' for clients that cannot set headers.");
        }
        catch (Exception ex)
        {
            _onStatus($"Could not copy to clipboard: {ex.Message}");
        }
    }

    /// <summary>
    /// Replaces the secret, keeping the lifetime the user chose but restarting it from now.
    /// Deliberately immediate and without a confirmation step: the only way to undo a leak is a
    /// new key, and the cost of an accidental click is re-copying it into one client's config.
    /// </summary>
    [RelayCommand]
    private void Regenerate()
    {
        Key.Regenerate();
        NotifyKeyChanged();

        _onChanged($"New proxy key generated for {_owner} — any client still using the old one "
                   + "now gets 403 until it is updated.");
    }

    /// <summary>Re-reads everything derived from the key after it is changed in place.</summary>
    private void NotifyKeyChanged()
    {
        OnPropertyChanged(nameof(Display));
        OnPropertyChanged(nameof(ExpirySummary));
        OnPropertyChanged(nameof(IsExpired));
    }
}
