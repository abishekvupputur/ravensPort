# Microsoft Store listing copy

Paste-ready text for the Partner Center **Store listing** page. Field limits are Partner Center's;
the counts in brackets are what the text below actually uses.

**This copy describes the Store build, not the EXE.** The MSIX has no Proton Pass and no mTLS —
see [STORE-MSIX.md](STORE-MSIX.md) for why — so neither is named here. Policy 10.1.5 applies to a
product *and its metadata*, which means the listing naming a CLI acquired outside the Store is the
same finding as the app doing it. The EXE still has both; [../README.md](../README.md) is the page
that describes it.

---

## Product name

```
RavensPort
```

## Short description

*Limit 1,000 characters — [279]*

```
Give each AI agent its own MCP endpoint. RavensPort is a tray-resident local proxy that owns the
OAuth2 flow for the APIs and MCP servers you use, pools several servers behind one endpoint, and
exposes only the tools you allow. Every secret lives in your own password manager, never on disk.
```

## Description

*Limit 10,000 characters — [2,847]*

```
MCP servers are increasingly locked behind OAuth2, but most MCP clients expect a bare HTTP endpoint
with no authentication story. And once an agent is connected to three servers, it sees all of their
tools — ninety of them — with no way to say "this agent gets these six."

RavensPort solves both halves. It runs a local reverse proxy on 127.0.0.1, holds the OAuth2 grants
and API keys for your upstreams, and lets you compose those upstreams into filtered, per-agent MCP
endpoints. Point an agent at http://127.0.0.1:5559/mcp/<name> and it sees exactly the toolset you
granted it — drawn from as many upstreams as you like, including ones it could never reach on its
own.

ROUTES
Attach a live OAuth2 token or a static API key to every request forwarded to an upstream. Your
client never handles authentication. Credentials can be placed anywhere the upstream expects them:
an Authorization header, any other header, or a request-body field, with a custom value prefix.
Credentials are never placed in a query string, where they would be written to the upstream's
access log. A route may attach none, one, or several credentials at once, in any mix, and the same
credential may appear in more than one place. Tokens refresh automatically ten minutes
before they expire.

FUNNELS
Pool several MCP servers behind a single local endpoint and pick exactly which tools, resources,
and prompts it exposes. Each agent gets its own funnel, so tightening what one agent can reach
never touches another.

A KEY PER ENDPOINT
Every route and every funnel carries its own proxy key with its own expiry. Other processes on your
machine cannot spend your grants, and a key leaked from one client cannot reach the rest.

YOUR SECRETS STAY IN YOUR PASSWORD MANAGER
OAuth client secrets, access and refresh tokens, API keys, proxy keys, and your whole configuration
are stored in a vault in 1Password — a vault you nominate and control. There is no
local cache and no fallback file. Nothing is written to this PC except redacted activity logs.
Because the configuration lives in the vault, one install supports as many profiles as you have
vaults.

While your password manager is locked, everything keeps working: edits, token refreshes, and key
rotation all proceed in memory and are written to the vault as soon as it is reachable again. A
locked manager never takes a route down.

DIAGNOSTICS YOU CAN READ
An in-app activity log records proxied requests, connects, refreshes, and vault operations. Query
parameter values are redacted and tokens are never logged. A vault integrity check accounts for
every item in the vault and changes nothing until you choose.

NO TELEMETRY
RavensPort contains no analytics, no crash reporting, and no update checks. It makes no network
connection you have not configured. The full source is public under the MIT License, and every
release is built by GitHub Actions with a build provenance attestation, so a download can be
verified against the workflow and commit that produced it.

BEFORE YOU INSTALL
RavensPort requires 1Password, with its command-line tool installed and signed in — it is where
your configuration is kept, and the proxy does not start without it. RavensPort does not install
it; setup inside the app checks what is present and walks through connecting a vault. Windows 10
or 11, 64-bit.
```

## Product features

*Limit 200 characters each, up to 20 — longest below is [148]*

```
Per-agent MCP endpoints that pool several servers and expose only the tools you pick
OAuth2 handled for you — Google, Nextcloud, or any custom provider, with automatic token refresh
Static API keys for services that never offered OAuth, with a Test button against a real endpoint
Credentials placed in any header or body field — never in a URL, with a custom value prefix
Any number of credentials per route, in any mix, including the same credential in several places
Every secret stored in your own 1Password vault — nothing written to this PC
A separate proxy key per route and per funnel, each with its own expiry
Keeps working while your password manager is locked, then syncs when it unlocks
Activity log with redaction and rotation, readable in the app
Vault integrity check that accounts for every item and changes nothing without your say-so
Tray-resident: keeps running when you close the window, survives provider and network errors
No telemetry, no analytics, no update checks — open source under the MIT License
```

## Search terms

*Limit 30 characters each, up to 7*

```
MCP
OAuth2
MCP proxy
AI agent tools
API key manager
local reverse proxy
1Password
```

## What's new in this version

*Limit 1,500 characters*

```
Version 3.0.1

- The page your browser lands on after signing in now says which way it went: a tick when the
  authorization completed, a cross and the provider's own reason when it did not. It previously
  reported success even when a sign-in had been declined.
- That page is also readable again — its text used to arrive garbled on some systems.

Version 3.0.0

- Renamed to RavensPort.
- Configuration and secrets now live in a 1Password vault instead of an encrypted
  file on this PC. There is no local cache and no fallback file.
- One vault per profile: connect a different vault to get a separate set of credentials, routes,
  and funnels from the same install.
- Vault integrity check accounts for every item in the vault and changes nothing until you pick.
- Everything keeps working while the vault is locked, and syncs when it unlocks.
- A separate proxy key per route and per funnel, each with its own expiry.

This version does not read configuration from versions before 2.0, and there is no import path.
Credentials, upstreams, routes, MCP sources, and funnels need to be set up again.
```

## System requirements

- Windows 10 or Windows 11, 64-bit (x64)
- 1Password (`op` 2.0 or newer), installed and signed in

## Supporting URLs

| Field | Value |
|---|---|
| Privacy policy | `https://github.com/abishekvupputur/ravensPort/blob/main/PRIVACY.md` |
| Website | `https://github.com/abishekvupputur/ravensPort` |
| Support contact | `https://github.com/abishekvupputur/ravensPort/issues` |
| Copyright | `Copyright (c) 2026 Abishek Narasimhan. MIT License.` |

## Screenshots

Partner Center wants at least one, 1366x768 or larger. **None of the four in `media/` qualify** —
they are window-sized captures, all under 1366 wide:

| File | Size |
|---|---|
| `credentialsScreen.png` | 916x807 |
| `mcpFunnelScreen.png` | 880x1033 |
| `routesScreen.png` | 920x839 |
| `settingsScreen.png` | 881x867 |

Upscaling them would look soft. Retake at 1366x768 or larger — maximise the window on a 1080p
display and capture the whole window, or capture the desktop and crop to at least 1366x768.
