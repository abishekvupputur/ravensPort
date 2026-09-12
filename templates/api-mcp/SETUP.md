# What each manifest needs

A manifest carries no host and no credential. Both live on the route you attach it to, which is why
the same file works against any route that reaches the same API. This is the table of what to
create on the **Routes** tab before importing one.

| Manifest | Route upstream | Credential | Placement |
| --- | --- | --- | --- |
| `google-routes.json` | `https://routes.googleapis.com` | Google Maps Platform API key | header `X-Goog-Api-Key` |
| `google-places.json` | `https://places.googleapis.com` | Google Maps Platform API key | header `X-Goog-Api-Key` |
| `google-weather.json` | `https://weather.googleapis.com` | Google Maps Platform API key | header `X-Goog-Api-Key` |
| `google-drive-readonly.json` | `https://www.googleapis.com/drive/v3` | Google OAuth, scope `drive.readonly` | header `Authorization`, prefix `Bearer ` |
| `brightsky-dwd.json` | `https://api.brightsky.dev` | none | — |
| `mvg-munich.json` | `https://www.mvg.de/api/bgw-pt/v3` | none | — |
| `transitous.json` | `https://api.transitous.org/api` | none | — |
| `tailscale-readonly.json` | `https://api.tailscale.com/api/v2` | Tailscale API access token | header `Authorization`, prefix `Bearer ` |
| `sample-task-tracker.json` | an invented API | — | — |

## The Google key

One Maps Platform key opens all three Google APIs here, but each is a separate host, so each needs
its own route. The key goes in as an **API key credential** on the Credentials tab, then attaches to
each route as the `X-Goog-Api-Key` header with no value prefix.

Enable the matching API in your Google Cloud project first — Routes, Places, and Weather are
separate products, and a key that works for one returns `REQUEST_DENIED` for the others until you
switch them on.

Drive is different: it is OAuth rather than a key, because it reads your own files. That credential
attaches as `Authorization` with the `Bearer ` prefix, which is RavensPort's default.

## Tailscale

Create the credential on the **Credentials** tab as an **API key**, and attach it to the route as
`Authorization` with the `Bearer ` prefix — RavensPort's default placement. Two kinds of secret work:

- An **API access token** from the [Keys page](https://login.tailscale.com/admin/settings/keys) of
  the admin console. It carries the permissions of the user who made it and expires in 1 to 90 days,
  so the bridge stops working on that date until you issue another.
- An **OAuth client**, as an app login with the client credentials grant against
  `https://api.tailscale.com/api/v2/oauth/token`. The client itself does not expire and RavensPort
  re-mints the short-lived token it issues, so nothing has to be replaced on a schedule. Grant only
  read scopes: `devices:core:read`, `devices:routes:read`, `devices:posture_attributes:read`,
  `users:read`, `dns:read`, `policy_file:read`, `services:read`, `feature_settings:read`,
  `webhooks:read`, `account_settings:read`, `logs:configuration:read`, `logs:network:read`, and the
  key-reading scopes `auth_keys:read` and `api_access_tokens:read`.

The manifest is read-only, but the credential is what actually enforces that: an access token made
by an admin can still write through any other route it is attached to. Scope the OAuth client to
reads if you want the limit to be real rather than a property of this one file.

Every path in the manifest uses `-` as the tailnet, which means "the tailnet this credential belongs
to". There is no tailnet argument for a model to get wrong, and a bridge reaches exactly one tailnet:
the one its credential came from. To read a second tailnet, add a second credential, route, and
bridge.

## The keyless three

Bright Sky, MVG, and Transitous need no credential at all. You still want them behind a route rather
than called directly, because that is what gives the bridge one base URL to be relative to — and it
is where you would add a key later if the service ever grows one.

Transitous asks for something instead of a key: an identifying `User-Agent` with a contact address.
The manifest sets one with a placeholder address. **Edit it to your own before using it**, so the
volunteers running the service can reach whoever is making the requests. It is also non-commercial
only; if this becomes work, self-host MOTIS.

## Why one manifest per host

A bridge goes through exactly one route, and a route has exactly one upstream. So the unit is the
base URL, not the vendor: three Google APIs on three hosts are three routes, three bridges, and
three manifests, even though one key opens all of them.

If you want an agent to see all of them as one server, that is what the **MCP Funnel** tab is for —
add each bridge as a source and pool them behind a single endpoint.
