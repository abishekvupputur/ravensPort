# API to MCP manifests

Starting points for the **API to MCP** tab. A manifest describes one API's operations as MCP tools;
RavensPort serves them at `/api-mcp/{slug}` and turns each call into one HTTP request through a
route you already configured, so that route's credential is attached for you.

| File | What it is |
| --- | --- |
| [`AUTHORING.md`](AUTHORING.md) | the format contract — read this before writing one, and give it to an agent you ask to write one |
| [`SETUP.md`](SETUP.md) | the route each manifest needs: base URL, credential, and where the credential goes |
| [`sample-task-tracker.json`](sample-task-tracker.json) | a worked example using every feature once, against an invented API. This is what **Load sample** inserts |
| [`google-drive-readonly.json`](google-drive-readonly.json) | Google Drive v3, read only: search, folders, metadata, export, sharing, revisions |
| [`google-routes.json`](google-routes.json) | Google Routes v2 — `https://routes.googleapis.com`, key as `X-Goog-Api-Key` |
| [`google-places.json`](google-places.json) | Google Places v1 — `https://places.googleapis.com`, key as `X-Goog-Api-Key` |
| [`google-weather.json`](google-weather.json) | Google Weather v1 — `https://weather.googleapis.com`, key as `X-Goog-Api-Key` |
| [`brightsky-dwd.json`](brightsky-dwd.json) | German weather and severe warnings — `https://api.brightsky.dev`, no credential |
| [`mvg-munich.json`](mvg-munich.json) | Munich transit — `https://www.mvg.de/api/bgw-pt/v3`, no credential |
| [`transitous.json`](transitous.json) | Worldwide transit — `https://api.transitous.org/api`, no credential |
| [`tailscale-readonly.json`](tailscale-readonly.json) | Tailscale API v2, read only: devices, users, routes, DNS, policy file, keys, audit logs — `https://api.tailscale.com/api/v2`, access token as `Authorization: Bearer` |

One manifest per base URL, because a bridge goes through exactly one route and a route has exactly
one upstream. The three Google APIs are separate hosts, so they are separate manifests even though
one key opens all of them. [`SETUP.md`](SETUP.md) lists what to configure for each.

## Using one

1. On the **Routes** tab, add a route to the API's base URL with a credential attached. For the
   Drive template that is `https://www.googleapis.com/drive/v3` with a Google credential holding
   `drive.readonly`.
2. On the **API to MCP** tab, press **Import file…**, pick the manifest, choose that route, and
   save. The editor shows every call the manifest would make before you commit to it.
3. Copy the bridge's proxy key from its row into your agent's MCP config.

Paths inside a manifest are relative to the route's prefix, so the same file works against any
route that reaches the same API.

## Checking one

```
dotnet run --project tools/RavensPort.ManifestCheck -- my-api.json
dotnet run --project tools/RavensPort.ManifestCheck -- templates/api-mcp
```

It prints every call each manifest would make and exits non-zero if anything is invalid. It calls
the same validator the app uses on import, so a file it passes is a file the app accepts.

The one thing it cannot check is whether your paths are right for your route — it has no way to
know which route you will attach the manifest to. Read the printed list against the API's own
documentation.

## Adding one here

Templates in this folder are validated by the test suite, so a broken one fails the build. Keep
them credential-free and, where the API allows it, read-only: these files get copied.
