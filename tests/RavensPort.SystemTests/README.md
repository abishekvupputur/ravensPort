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

1. The vault is empty at startup.
2. Seeds a credential, two mock MCP servers, an upstream, a route with the credential attached, and
   two funnels — one pooling both sources, one exposing a single source.
3. The route forwards **and the credential arrives at the upstream** — asserted on what the upstream
   received, because a status code alone would pass for a route that forwarded nothing.
4. Both funnels answer on the current revision **and** on `2025-11-25`, with the negotiated version
   asserted first so a pin that quietly failed cannot make the old-protocol half decorative.
5. mTLS is switched on, a certificate minted and stored, and the PFX written to disk as the Settings
   tab's download would.
6. The host is torn down and rebuilt from nothing but the token — so anything the second host knows
   came back out of 1Password.
7. Every check in 3 and 4 runs again over https with the client certificate.
8. The vault is emptied again.

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
