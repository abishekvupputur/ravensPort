namespace RavensPort.SystemTests;

/// <summary>
/// What this suite needs from the environment, and the guard that stops it running anywhere it
/// should not.
///
/// The token is read from the environment and never from a file, an argument, or a constant: it is
/// a bearer credential for a whole 1Password account, and the two places it must not end up are the
/// repository and a shell history. CI holds it as a secret; a developer exports it for one run.
///
/// <see cref="Acknowledgement"/> exists because the danger here is not the token, it is the vault.
/// This suite deletes every RavensPort item it finds before it starts, because "the vault is empty
/// at startup" is the first thing it is asserting. Run it against the wrong account and it destroys
/// the credentials, routes and funnels of a working install. A token alone is too easy to have
/// lying around in a shell; a second variable that spells out what is about to happen is not.
/// </summary>
internal static class SystemTestEnvironment
{
    public const string TokenVariable = "OP_SERVICE_ACCOUNT_TOKEN";

    /// <summary>
    /// Must be set to exactly <c>i-understand-this-erases-the-ravensport-vault</c>. Deliberately
    /// long, deliberately unpleasant to type, and deliberately not a boolean: nobody sets this by
    /// reflex, which is the entire point.
    /// </summary>
    public const string AcknowledgementVariable = "RAVENSPORT_SYSTEM_TEST_ACK";

    public const string AcknowledgementValue = "i-understand-this-erases-the-ravensport-vault";

    public static string? Token => Get(TokenVariable);

    private static string? Acknowledgement => Get(AcknowledgementVariable);

    /// <summary>
    /// Why the suite cannot run, in a sentence the person who tried can act on, or null when it can.
    ///
    /// Returned rather than thrown so the fact attribute can turn it into a Skip reason. A skipped
    /// test says "this did not run"; a passing one that quietly returned early says "this is fine",
    /// and for a suite whose whole job is to prove the product works, those must not look alike.
    /// </summary>
    public static string? WhyUnavailable()
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            return $"{TokenVariable} is not set. This suite needs a 1Password service-account token "
                   + "for an account whose only vault is a throwaway one named 'RavensPort' -- the "
                   + "vault name is a constant in the product (VaultConstants.VaultName), so the "
                   + "token is what decides which account is touched.";
        }

        if (!string.Equals(Acknowledgement, AcknowledgementValue, StringComparison.Ordinal))
        {
            return $"{AcknowledgementVariable} is not set to '{AcknowledgementValue}'. This suite "
                   + "erases every RavensPort item in the vault the token can reach, because it "
                   + "asserts the vault starts empty. Set it only when the token points at an "
                   + "account you are willing to lose.";
        }

        return null;
    }

    private static string? Get(string name) => Environment.GetEnvironmentVariable(name);
}

/// <summary>
/// A <see cref="FactAttribute"/> that skips rather than fails when the environment is not set up.
///
/// The reason is set in the constructor, which xUnit reads at discovery, so an unconfigured machine
/// reports these as skipped with the explanation attached instead of erroring on a missing token.
/// </summary>
internal sealed class RequiresServiceAccountFactAttribute : FactAttribute
{
    public RequiresServiceAccountFactAttribute()
    {
        if (SystemTestEnvironment.WhyUnavailable() is { } reason)
        {
            Skip = reason;
        }
    }
}
