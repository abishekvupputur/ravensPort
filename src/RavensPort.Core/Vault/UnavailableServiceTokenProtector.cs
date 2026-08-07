namespace RavensPort.Core.Vault;

/// <summary>
/// The portable build's service-token store: there isn't one yet.
///
/// Nothing is ever kept, so nothing is found and nothing is forgotten. The setup page gates the
/// offer on Hello being available, which it is not here, so the checkbox that would reach
/// <see cref="ProtectOnePasswordTokenAsync"/> is never shown — and that method throws rather than
/// quietly doing nothing, so a route to it that is added later fails loudly instead of telling the
/// user their token is saved when it is not.
///
/// Replaced when the keyring lands — see .claude/LINUX-PORT-PLAN.md. Until then a Linux user pastes
/// the token once per run, which is stated on the page.
/// </summary>
internal sealed class UnavailableServiceTokenProtector : IServiceTokenProtector
{
    private const string Message =
        "RavensPort cannot keep a 1Password service account token on this platform yet. Paste it "
        + "again after a restart.";

    public bool HasProtectedOnePasswordToken() => false;

    public Task ProtectOnePasswordTokenAsync(string token) =>
        throw new PlatformNotSupportedException(Message);

    public Task<string?> UnprotectOnePasswordTokenAsync() => Task.FromResult<string?>(null);

    public Task ForgetOnePasswordTokenAsync() => Task.CompletedTask;
}
