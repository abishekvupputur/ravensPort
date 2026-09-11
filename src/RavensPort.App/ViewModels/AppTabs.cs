namespace RavensPort.App.ViewModels;

/// <summary>
/// The tabs of the main window, as one dependency.
///
/// Everything that needs them needs all of them, and for one reason: after a vault is connected,
/// disconnected, or reloaded, every tab is showing rows built from a store that no longer exists.
/// Taking them individually made a constructor grow by one parameter per tab and said nothing
/// about why they travel together.
/// </summary>
public sealed class AppTabs(
    CredentialsViewModel credentials,
    RoutesViewModel routes,
    ApiBridgeViewModel apiBridges,
    McpFunnelViewModel funnels,
    SettingsViewModel settings)
{
    public CredentialsViewModel Credentials { get; } = credentials;
    public RoutesViewModel Routes { get; } = routes;
    public ApiBridgeViewModel ApiBridges { get; } = apiBridges;
    public McpFunnelViewModel Funnels { get; } = funnels;
    public SettingsViewModel Settings { get; } = settings;

    /// <summary>
    /// Rebuilds every tab from whatever the store now holds.
    ///
    /// In tab order, which is also roughly dependency order: a route's credential picker is filled
    /// from the credentials tab's records, and both the bridges and the funnel list routes.
    /// </summary>
    public void ReloadAll()
    {
        Credentials.Reload();
        Routes.Reload();
        ApiBridges.Reload();
        Funnels.Reload();
        Settings.Reload();
    }
}
