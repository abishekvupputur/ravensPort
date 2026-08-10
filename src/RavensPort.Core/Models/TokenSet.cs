namespace RavensPort.Core.Models;

/// <summary>
/// A token as the app holds it.
///
/// <paramref name="ExpiresAtUtc"/> is nullable because not every provider advertises an expiry.
/// A GitHub OAuth App token has no <c>expires_in</c> at all and never ages out on its own; when
/// this was a plain <see cref="DateTimeOffset"/>, a missing <c>expires_in</c> arrived as zero
/// seconds, so the token was recorded as expiring the instant it was issued and the UI showed a
/// perfectly good credential as "Expired" forever. Null means "no expiry advertised" and is
/// treated as never expiring, which is what the provider actually said.
/// </summary>
public sealed record TokenSet(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAtUtc,
    string TokenType,
    DateTimeOffset ObtainedUtc)
{
    public bool IsExpiringWithin(TimeSpan window) =>
        ExpiresAtUtc is { } expiry && expiry - DateTimeOffset.UtcNow < window;

    /// <summary>One phrase for the UI and the log, so "never expires" is not spelled two ways.</summary>
    public string DescribeExpiry() => ExpiresAtUtc is { } expiry
        ? $"expires {expiry.ToLocalTime():g}"
        : "no expiry advertised";
}
