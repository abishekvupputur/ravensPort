using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace RavensPort.Core.Models;

/// <summary>
/// The secret one local endpoint — a route or an MCP funnel — requires of its callers, together
/// with when it stops being accepted.
///
/// One key per endpoint rather than one for the whole proxy. A single shared key made every
/// client that held it a client of *every* route: an agent given the key so it could reach a
/// calendar endpoint could equally spend the OAuth grant attached to a mail endpoint, and
/// revoking one client's access meant re-keying all of them. Per-endpoint keys make the blast
/// radius of a leaked key exactly the endpoint it was issued for, and make revocation a
/// single-row operation.
///
/// Expiry is opt-in per key. <see cref="ExpiresUtc"/> null means the key is valid until it is
/// regenerated or the endpoint is deleted, which is the right default for a machine-local tool
/// whose clients are config files nobody rotates.
/// </summary>
public sealed class ProxyKey
{
    /// <summary>
    /// The secret itself. Deliberately empty by default rather than generated in an initializer:
    /// an initializer would hand every *load* a fresh value, which then looks "already set" to
    /// the backfill in ConfigStoreCache and never reaches disk — so each restart would invent a
    /// different key and silently break every configured client. Generation happens either when
    /// the endpoint is created or in that one backfill, each immediately followed by a save.
    /// </summary>
    public string Value { get; set; } = "";

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the key stops being accepted. Null means it never expires.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? ExpiresUtc { get; set; }

    /// <summary>A fresh secret with the given lifetime, or one that never expires when null.</summary>
    public static ProxyKey Generate(TimeSpan? lifetime = null)
    {
        var now = DateTimeOffset.UtcNow;

        return new ProxyKey
        {
            Value = GenerateValue(),
            CreatedUtc = now,
            ExpiresUtc = lifetime is { } span ? now + span : null,
        };
    }

    /// <summary>
    /// 256 bits from the OS CSPRNG, base64url-encoded so it survives a query string, a JSON
    /// config file, and a shell command line without escaping.
    /// </summary>
    public static string GenerateValue() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Replaces the secret in place, keeping the current expiry policy's *length* and restarting
    /// the clock from now. Regenerating is the one action that resets the countdown: the key it
    /// hands back is new, so every client has to be reconfigured anyway, and a fresh key that
    /// inherited the old expiry could be dead on arrival.
    /// </summary>
    public void Regenerate()
    {
        var lifetime = ExpiresUtc is { } expiry ? expiry - CreatedUtc : (TimeSpan?)null;

        CreatedUtc = DateTimeOffset.UtcNow;
        Value = GenerateValue();
        ExpiresUtc = lifetime is { } span ? CreatedUtc + span : null;
    }

    /// <summary>
    /// Sets a new lifetime measured from when the key was issued; null makes it never expire.
    ///
    /// Deliberately anchored on <see cref="CreatedUtc"/> rather than on now, so re-picking in the
    /// drop-down cannot silently extend the life of a secret that has already been in circulation
    /// for a while — "1 hour" means one hour of this key existing, whenever it was chosen. The
    /// consequence is that shortening the window on an older key can expire it immediately, and
    /// that a lapsed key is revived only by <see cref="Regenerate"/>, which issues a new secret.
    /// </summary>
    public void SetLifetime(TimeSpan? lifetime) =>
        ExpiresUtc = lifetime is { } span ? CreatedUtc + span : null;

    [JsonIgnore]
    public bool IsConfigured => !string.IsNullOrEmpty(Value);

    public bool IsExpired(DateTimeOffset now) => ExpiresUtc is { } expiry && now >= expiry;

    /// <summary>Short status for a grid cell, e.g. "never expires" or "expires in 12 days".</summary>
    public string DescribeExpiry(DateTimeOffset now)
    {
        if (ExpiresUtc is not { } expiry) return "never expires";
        if (now >= expiry) return $"expired {expiry.ToLocalTime():yyyy-MM-dd HH:mm}";

        var remaining = expiry - now;

        // Never "0 minute(s)": anything under a minute is still time left, and rounding it away
        // would report a live key as though it had already gone.
        var span = remaining switch
        {
            { TotalDays: >= 1 } => $"{(int)remaining.TotalDays} day(s)",
            { TotalHours: >= 1 } => $"{(int)remaining.TotalHours} hour(s)",
            _ => $"{Math.Max(1, (int)remaining.TotalMinutes)} minute(s)",
        };

        return $"expires in {span} ({expiry.ToLocalTime():yyyy-MM-dd HH:mm})";
    }
}

/// <summary>One entry in the "how long is this key valid" picker.</summary>
public sealed record ProxyKeyLifetime(string Label, TimeSpan? Duration)
{
    public static IReadOnlyList<ProxyKeyLifetime> All { get; } =
    [
        new("Never expires", null),
        new("1 hour", TimeSpan.FromHours(1)),
        new("4 hours", TimeSpan.FromHours(4)),
        new("1 day", TimeSpan.FromDays(1)),
        new("7 days", TimeSpan.FromDays(7)),
        new("30 days", TimeSpan.FromDays(30)),
        new("90 days", TimeSpan.FromDays(90)),
        new("360 days", TimeSpan.FromDays(360)),
    ];

    public static ProxyKeyLifetime Never => All[0];

    /// <summary>
    /// The entry that best describes a key's current setting, so reopening the tab shows what was
    /// chosen rather than resetting the picker. Matched on the configured *length* (expiry minus
    /// creation) rather than on time remaining, which shrinks every second.
    /// </summary>
    public static ProxyKeyLifetime ForKey(ProxyKey key)
    {
        if (key.ExpiresUtc is not { } expiry) return Never;

        var configured = expiry - key.CreatedUtc;

        return All.FirstOrDefault(option =>
                   option.Duration is { } duration
                   && Math.Abs((duration - configured).TotalMinutes) < 1)
               ?? new ProxyKeyLifetime($"until {expiry.ToLocalTime():yyyy-MM-dd HH:mm}", configured);
    }

    public override string ToString() => Label;
}
