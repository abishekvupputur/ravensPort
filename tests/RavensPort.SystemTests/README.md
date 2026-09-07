# System approval suite

One ordered pass over the product against a **real 1Password vault**, from an empty vault to mTLS
and back. Everything else in `tests/` runs against an in-memory vault; this is the only thing that
exercises the vault, and it is the only thing in the repository with side effects outside its own
temp directory.

It runs in CI from `.github/workflows/system-approval.yml`, where the token and vault names come from
repository secrets. Locally it does nothing during `dotnet test` unless you set it up deliberately:
without the environment below every stage reports **Skipped**, never passed.

The pass is **eleven ordered tests**, not one. Each names what it proves, so a CI log lists the
stages whether or not anything failed, and a failure says which stage — the earlier version ran the
same sequence as a single method and reported `Total tests: 1`. The order is enforced by
`StageOrderer` and the state is carried by `ApprovalRun`; when a stage fails, the ones after it say
which prerequisite they were waiting on instead of failing on its wreckage.

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

# Only needed if the vault is not called "RavensPort". The provider normally finds its vault by the
# Config item stamped inside it rather than by name, so any name works -- until that stamp is gone,
# which is the one case this is consulted for.
$env:RAVENSPORT_SYSTEM_TEST_VAULT = "RavensPort CI"

# Optional. Defaults to Beeceptor's shared OAuth sandbox; point it at a local stub to stop the run
# depending on anything outside the machine.
$env:RAVENSPORT_SYSTEM_TEST_OAUTH_TOKEN_ENDPOINT = "https://oauth-mock.mock.beeceptor.com/oauth/token/github"

dotnet test tests/RavensPort.SystemTests/RavensPort.SystemTests.csproj
```

**1Password rate-limits vault writes** — 100 an hour per service account — and one pass spends a
couple of dozen. Runs back to back will eventually be throttled.

There is exactly one sweep, at the start. A run leaves its records behind and the *next* run deletes
them, which is why the first thing asserted is that the vault is empty. Cleaning up at the end as
well would write a cleared store and then delete every item a second time, spending quota on work the
opening sweep does anyway — and nothing between two runs reads the vault, so there is nobody for the
tidier ending to be tidy for.

### Spreading the load over more than one account

The limit is per account, so a second one halves the rate either sees. Add it with a `_2` suffix —
its own service account, its own vault:

```powershell
$env:OP_SERVICE_ACCOUNT_TOKEN_2 = "ops_..."
$env:RAVENSPORT_SYSTEM_TEST_VAULT_2 = "RavensPort CI 2"
```

Before anything else, the suite asks each configured vault when it was last written and uses
whichever has gone longest. That is read from the newest item's `updatedAt`, so choosing costs no
write quota — it cannot consume the thing it exists to conserve. A vault holding nothing, or only
the Config stamp, counts as never written and wins outright.

Alternating blindly would be simpler and wrong: CI runners keep no state between runs, so there is
nowhere to remember whose turn it is. The vaults themselves remember.

Numbering must be contiguous. The suite stops at the first missing token, so a typo in
`OP_SERVICE_ACCOUNT_TOKEN_3` means "there is no third account" rather than an error. An account that
cannot be reached is skipped with a note and costs only its turn.

The acknowledgement variable is deliberately long and deliberately not a boolean. A token alone is
too easy to have sitting in a shell; nobody sets this one by reflex.

## What it covers

The numbers below are the stage numbers in the test names.

1. `Stage01_TheVaultStartsEmpty` — **the vault is empty at startup**. Every item is deleted one by
   one, not just the ones the store knows about: a save only reconciles what it loaded, so records
   left by the previous run, or anything added by hand while testing, would otherwise survive and
   make "empty at startup" assert against a vault that is nothing of the kind. The sweep runs
   *before* the store is read, because an item the vault will not hand back fails the load and a host
   that cannot start cannot run the sweep that would have fixed it. The Config item is spared — it is
   the stamp that identifies the vault rather than data in it, and deleting it leaves not an empty
   vault but an unrecognisable one.
2. `Stage02_CredentialsRoutesAndFunnelsAreSeeded` — three credentials (an OAuth grant, a static
   project key and a client-credentials grant), three mock MCP servers, two upstreams, **eight routes
   covering every credential placement**, and three funnels: one pooling two sources, one exposing a
   single source, one reaching an MCP server through a credentialed route.
3. `Stage03_EveryCredentialPlacementReachesTheUpstream` — **the credential matrix**, checked on what
   the upstream actually received rather than on a status code, because a 200 would pass for a route
   that forwarded nothing:

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
4. `Stage04_BothFunnelsAnswerOnBothProtocolRevisions` — both funnels answer on the current revision
   **and** on `2025-11-25`, with the negotiated version asserted first so a pin that quietly failed
   cannot make the old-protocol half decorative.
5. `Stage05_TheIssuedOAuthTokenReachesTheMcpServer` — **a real OAuth2 exchange**, and the token
   followed to an MCP server. A client-credentials grant runs against a mock authorization server —
   the only grant that works unattended, since the browser flow needs someone at a consent screen and
   the device flow someone at a second device. The issued token is attached to a route whose upstream
   *is* an MCP server, and the funnel reaches it as a `ProxyRoute` source, so the credential
   transform sits in the path. The fake records the `Authorization` of every request it receives, and
   every one must carry the issued token — a transform that attached it to some requests but not
   others would still satisfy a "contains" check while leaving real calls unauthenticated. The
   proxy's own key must not be among what was forwarded.
6. `Stage06_MtlsIsEnabledAndTheCertificateExported` — mTLS is switched on, a certificate minted and
   stored, and the PFX written to disk as the Settings tab's download would.
7. `Stage07_TheConfigurationSurvivesARestart` — the host is torn down and rebuilt from nothing but
   the token, so everything the second host knows came back out of 1Password: eight routes, three
   funnels, three sources, three credentials, the mTLS setting and the issued access token.
8. `Stage08_…`, `Stage09_…`, `Stage10_…` — 3, 4 and 9 run **again** over https with the client
   certificate, against records that survived the restart.
9. `Stage11_TheListenerRefusesTheWrongCaller` — **the listener refuses the wrong caller**, which is
   the only thing that shows mTLS is enforced rather than merely switched on:

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
9. **A real OAuth2 exchange**, and the token followed to an MCP server. A client-credentials grant
   runs against a mock authorization server — the only grant that works unattended, since the
   browser flow needs someone at a consent screen and the device flow someone at a second device.
   The issued token is then attached to a route whose upstream *is* an MCP server, and the funnel
   reaches it as a `ProxyRoute` source, so the credential transform sits in the path. The fake
   records the `Authorization` of every request it receives, and every one must carry the issued
   token — a transform that attached it to some requests but not others would still satisfy a
   "contains" check while leaving real calls unauthenticated. The proxy's own key must not be
   among what was forwarded.

   Run before mTLS and again after the restart, where the host never performed an exchange at all
   and the token came back out of 1Password.
10. The vault is emptied again, item by item — except the Config item, which is the stamp that
    identifies the vault rather than data in it. Deleting that does not leave an empty vault, it
    leaves an unrecognisable one.

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
