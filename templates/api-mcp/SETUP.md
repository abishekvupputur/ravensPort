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
