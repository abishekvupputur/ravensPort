# System approval suite

One ordered pass over the product against a **real 1Password vault**, from an empty vault to mTLS
and back. Everything else in `tests/` runs against an in-memory vault; this is the only thing that
exercises the vault, and it is the only thing in the repository with side effects outside its own
temp directory.

It does not run in CI and does not run during `dotnet test` unless you set it up deliberately.
Without the environment below it reports **Skipped**, never passed.

## Before you run it

You need a 1Password **service account** token. The vault name is a constant in the product
(`VaultConstants.VaultName` is `"RavensPort"`), so the token — not a setting — is what decides which
account gets touched.

> **This suite deletes every RavensPort item in the vault it can reach.** "The vault is empty at
> startup" is the first thing it asserts. Point it at a throwaway account whose only vault is a test
> `RavensPort`. Pointed at a working install, it destroys those credentials, routes and funnels.

```powershell
$env:OP_SERVICE_ACCOUNT_TOKEN = "ops_..."       # never commit this, never paste it in a PR
$env:RAVENSPORT_SYSTEM_TEST_ACK = "i-understand-this-erases-the-ravensport-vault"

dotnet test tests/RavensPort.SystemTests/RavensPort.SystemTests.csproj
```

The acknowledgement variable is deliberately long and deliberately not a boolean. A token alone is
too easy to have sitting in a shell; nobody sets this one by reflex.

## What it covers

1. **The vault is empty at startup** — every item is deleted one by one, not just the ones the store
   knows about. A save only reconciles what it loaded, so items from a run that failed before its
   cleanup, or anything added by hand while testing, would otherwise survive and make "empty at
   startup" assert against a vault that is nothing of the kind.
2. Seeds two credentials (an OAuth grant and a static project key), two mock MCP servers, an
   upstream, **seven routes covering every credential placement**, and two funnels — one pooling
   both sources, one exposing a single source.
3. **The credential matrix**, checked on what the upstream actually received rather than on a status
   code, because a 200 would pass for a route that forwarded nothing:

   | Route | What must arrive |
   |---|---|
   | `/app/none` | nothing — and the caller's own `Authorization` is stripped, not passed through |
   | `/app/one` | `Authorization: Bearer <token>` |
   | `/app/two-headers` | `Authorization: Bearer <A>` + `X-Project-Key: <B>` |
   | `/app/several-headers` | `Authorization: Bearer <A>` + `X-Api-Key: <B>` + `PRIVATE-TOKEN: token <B>` |
   | `/app/header-body` | `Authorization: Bearer <A>` + `{"auth_token": "<B>"}` |
   | `/app/two-body` | `{"access_token": "<A>", "project_token": "<B>"}` — in one rewrite |
   | `/app/oauth-plus-key` | `Authorization: Bearer <token>` + `X-Api-Key: <key>` |

   There is no query row. `CredentialPlacement.Query` is no longer permitted: a secret in a URL is
   written to the upstream's access log, every intermediary's, and browser history.
4. Both funnels answer on the current revision **and** on `2025-11-25`, with the negotiated version
   asserted first so a pin that quietly failed cannot make the old-protocol half decorative.
5. mTLS is switched on, a certificate minted and stored, and the PFX written to disk as the Settings
   tab's download would.
6. The host is torn down and rebuilt from nothing but the token — so anything the second host knows
   came back out of 1Password.
7. Everything in 3 and 4 runs **again** over https with the client certificate.
8. **The listener refuses the wrong caller**, which is the only thing that shows mTLS is enforced
   rather than merely switched on:

   | Caller | Result |
   |---|---|
   | as configured | HTTP 200 |
   | no client certificate | refused in the handshake — `Win32Exception`, no request ever sent |
   | listener not trusted | refused by the caller — `AuthenticationException … UntrustedRoot` |
   | wrong PFX passphrase | refused before any connection — `CryptographicException` |

   Asserted by kind, not by message. A Node client sees `DEPTH_ZERO_SELF_SIGNED_CERT`,
   `ERR_SSL_SSLV3_ALERT_CERTIFICATE_UNKNOWN` and `mac verify failure` for these same three; those
   are OpenSSL's strings surfaced by Node, and .NET words them differently. Pinning them would pin
   the client library rather than this product, so the actual message is logged instead.
9. The vault is emptied again, item by item.

## What it does not cover, and why

**The installer and the GUI.** Not an omission — unattended operation is impossible by design.
`OnePasswordSession` refuses to persist a service-account token ("an install that starts at login
serves nothing until someone types the token in"), and the one place a token *can* be kept,
`HelloKeyProtector`, needs a Windows Hello gesture to give it back and "cannot do so quietly". So an
installed `RavensPort.exe` started without a human has no vault and serves nothing.

This suite therefore hosts the same pipeline in-process — the same guard, funnel gate, funnel
endpoints and YARP, in the same order — against the real vault. That covers everything between the
vault and the wire. Driving the installed app would need a person for two gestures, which is a
different kind of test and cannot run in CI.
