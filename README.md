<p align="center">
  <img src="media/logo.png" alt="RavensPort" width="140">
</p>

<h1 align="center">RavensPort</h1>

<p align="center">
  <b>Give each AI agent its own MCP endpoint — pooling the servers you choose, exposing only the
  tools you allow, with OAuth handled for you.</b>
</p>

<p align="center">
  <a href="https://github.com/abishekvupputur/ravensPort/actions/workflows/coverage.yml"><img alt="Build status"
    src="https://img.shields.io/github/actions/workflow/status/abishekvupputur/ravensPort/coverage.yml?branch=main&label=build&logo=githubactions&logoColor=white&style=flat-square"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=abishekvupputur_ravensPort"><img alt="SonarCloud quality gate"
    src="https://img.shields.io/sonar/quality_gate/abishekvupputur_ravensPort?label=quality%20gate&server=https%3A%2F%2Fsonarcloud.io&logo=sonarqubecloud&logoColor=white&style=flat-square"></a>
  <a href="https://sonarcloud.io/summary/new_code?id=abishekvupputur_ravensPort"><img alt="Line coverage"
    src="https://img.shields.io/sonar/coverage/abishekvupputur_ravensPort?server=https%3A%2F%2Fsonarcloud.io&logo=sonarqubecloud&logoColor=white&style=flat-square"></a>
  <a href="../../releases"><img alt="Latest release"
    src="https://img.shields.io/github/v/release/abishekvupputur/ravensPort?label=release&logo=github&logoColor=white&style=flat-square"></a>
  <a href="LICENSE"><img alt="MIT licence"
    src="https://img.shields.io/github/license/abishekvupputur/ravensPort?label=licence&color=blue&style=flat-square"></a>
</p>

<p align="center">
  <code>winget install RavensPort</code>
</p>

<p align="center">
  <a href="https://github.com/abishekvupputur/ravensPort/wiki"><b>Documentation</b></a>
  &nbsp;·&nbsp;
  <a href="https://apps.microsoft.com/detail/9PBNQH53L61D"><b>Microsoft Store</b></a>
  &nbsp;·&nbsp;
  <a href="../../releases"><b>Releases</b></a>
</p>

<p align="center">
  <sub>winget and the installer give you the full app. The Store build has no Proton Pass and no
  mTLS — <a href="https://github.com/abishekvupputur/ravensPort/wiki/Installation#from-the-microsoft-store">why</a>.</sub>
</p>

A tray-resident Windows app that runs a local reverse proxy on `127.0.0.1`. It owns the OAuth2
flow and token lifecycle for upstream APIs and MCP servers, then lets you compose those servers
into filtered, per-agent MCP endpoints.

[![MCP Funnel tab](media/mcpFunnelScreen.png)](media/mcpFunnelScreen.png)

## What it does

- **MCP Funnel** — per-agent endpoints pooling several MCP servers, with per-tool filtering
- **API to MCP** — turn an API you already proxy into an MCP endpoint from a JSON manifest, or
  import an OpenAPI document and pick which of its operations become tools
- **OAuth2, handled** — Google, GitHub, Nextcloud or any custom provider; device-code sign-in;
  client-credentials and Google service accounts for logins with nobody at the keyboard; static
  API keys for the services that never offered OAuth. Tokens refresh in the background
- **Secrets stay in your password manager** — 1Password or Proton Pass holds every credential,
  and nothing is written to this PC
- **A proxy key per endpoint** — every route, funnel and bridge has its own, so a key leaked from
  one client cannot reach the rest. Optionally require a client certificate as well

## Install

```powershell
winget install RavensPort
```

Or take the installer from [Releases](../../releases), or the
[Microsoft Store](https://apps.microsoft.com/detail/9PBNQH53L61D). Windows 10/11. Full
instructions, including building from source, are in
[Installation](https://github.com/abishekvupputur/ravensPort/wiki/Installation).

## Documentation

Everything lives in the **[wiki](https://github.com/abishekvupputur/ravensPort/wiki)**.

| Page | |
|---|---|
| [Concepts](https://github.com/abishekvupputur/ravensPort/wiki/Concepts) | How credentials, routes, funnels and bridges fit together |
| [Setting up a credential](https://github.com/abishekvupputur/ravensPort/wiki/Credentials) | Every sign-in method, and how to test one |
| [Setting up a route](https://github.com/abishekvupputur/ravensPort/wiki/Routes) | Proxying an upstream, and choosing how the credential is sent |
| [API to MCP](https://github.com/abishekvupputur/ravensPort/wiki/API-to-MCP) | Manifests, variants, and importing an OpenAPI document |
| [MCP Funnel](https://github.com/abishekvupputur/ravensPort/wiki/MCP-Funnel) | Pooling sources into one endpoint and filtering what it exposes |
| [Calling the proxy](https://github.com/abishekvupputur/ravensPort/wiki/Calling-the-proxy) | Proxy keys, how long they last, and why there is one per endpoint |
| [Client certificates (mTLS)](https://github.com/abishekvupputur/ravensPort/wiki/Client-certificates) | Requiring a certificate as well as a key |
| [Where your configuration lives](https://github.com/abishekvupputur/ravensPort/wiki/Configuration-and-storage) | What is in the vault, what is on disk, and what syncs |
| [Logs](https://github.com/abishekvupputur/ravensPort/wiki/Logs) | What is recorded, what is redacted, and where it goes |
| [Troubleshooting](https://github.com/abishekvupputur/ravensPort/wiki/Troubleshooting) | When something does not work |
| [Building](https://github.com/abishekvupputur/ravensPort/wiki/Building) | Building, testing and packaging from source |

Manifests ready to use are in [`templates/api-mcp/`](templates/api-mcp/), and the format is
documented in [AUTHORING.md](templates/api-mcp/AUTHORING.md).

## License

MIT — see [LICENSE](LICENSE). Third-party dependencies (all MIT or Apache-2.0) are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which also covers what redistributing the
published exe requires.
