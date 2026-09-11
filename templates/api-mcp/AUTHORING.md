# Writing an API to MCP manifest

Instructions for whoever — or whatever — writes one of these. If you are an AI agent asked to turn
an API into a RavensPort bridge, this file is the contract. Read it before writing JSON, and run
the validator before handing the result over.

## What you are producing

A JSON document describing one API's operations as MCP tools. RavensPort serves it at
`/api-mcp/{slug}`, and each tool call becomes **one HTTP request through a route the user has
already configured**. That route carries the credential.

You are describing *what to call and what to call it*. You are not handling authentication, base
URLs, hosts, or tokens.

## The rules that will get you rejected

Work through these before anything else. Each one is enforced at import, and a manifest that
breaks one is refused whole.

1. **Never put a credential anywhere in a manifest.** No API keys, no tokens, no `Authorization`
   header, no `access_token` query parameter. The route attaches the credential one hop later. A
   manifest that sets `Authorization`, `Cookie`, `Content-Type`, `Host`, `Content-Length`,
   `Transfer-Encoding`, `Connection`, `Upgrade`, or any `X-Proxy-*` header is refused.
2. **Paths are relative to the route's prefix, and must start with `/`.** Not absolute URLs, not
   protocol-relative (`//host/x`), no `..` segments, no `%` escapes, no `?` or `#` — the query map
   owns parameters. If the API's docs say `GET https://api.example.com/v2/things`, and the user's
   route points at `https://api.example.com/v2`, your path is `/things`.
3. **Tool names**: `[A-Za-z0-9_-]`, at most 94 characters. Lowercase with underscores reads best to
   a model. They must be unique, case-insensitively.
4. **Every tool needs an `inputSchema`** that is a JSON Schema object with `"type": "object"`, or no
   `inputSchema` at all if it takes no arguments. Anything else is refused.
5. **A tool has either `request` or `variants`, never both.**
6. **No body on `GET` or `HEAD`.**
7. Methods are limited to `GET`, `HEAD`, `POST`, `PUT`, `PATCH`, `DELETE`.

## How arguments reach the request

A placeholder is `{name}`, matching a top-level property of `inputSchema`.

| Where you put it | If the model omits that argument | If the argument is an object or a list |
| --- | --- | --- |
| `path` | the call is **refused**, naming the argument | refused |
| `query` value | that parameter is left out entirely | refused |
| `headers` value | that header is left out entirely | refused |
| `body` template | that property is left out | allowed, but only when the value is exactly `"{name}"` |

**Absent means omit, except in the path, where absent means refuse.** So declare optionality once,
in the schema's `required` list, and let the template follow. Never write a template that would
need a placeholder to expand to nothing useful — `"/things/{id}"` with `id` optional is a
contradiction the validator cannot catch but the user will hit on the first call.

Path and query values are URL-escaped for you. Do not pre-escape anything.

### Bodies

```json
"bodyMode": "none"       // the default
"bodyMode": "template"   // send "body", with placeholders substituted
"bodyMode": "arguments"  // send every argument the path, query, headers and selector did not use
```

In a `template` body, a string that is **exactly** one placeholder is replaced by that argument's
whole JSON value, so `"tags": "{tags}"` sends a real array. A string that merely *contains* one is
interpolated as text.

## Collapsing similar endpoints into one tool

Prefer **one tool with variants** over three tools whose descriptions differ by a word. A model
picks reliably between values of one argument; it picks badly between near-identical tools.

```json
{
  "name": "list_by_kind",
  "description": "Lists files of one kind.",
  "inputSchema": {
    "type": "object",
    "properties": {
      "kind": { "type": "string", "enum": ["documents", "spreadsheets", "folders"] }
    },
    "required": ["kind"]
  },
  "variantBy": "kind",
  "variants": {
    "documents":    { "description": "Google Docs",   "method": "GET", "path": "/files", "query": { "q": "mimeType='application/vnd.google-apps.document'" } },
    "spreadsheets": { "description": "Google Sheets",  "method": "GET", "path": "/files", "query": { "q": "mimeType='application/vnd.google-apps.spreadsheet'" } },
    "folders":      { "description": "Folders",        "method": "GET", "path": "/files", "query": { "q": "mimeType='application/vnd.google-apps.folder'" } }
  }
}
```

Three rules here, all enforced:

- `variantBy` must name a property the schema declares, as a **string with an `enum`**.
- That enum must list **exactly** the variant keys. An extra enum value is a choice that always
  fails; an extra variant is a call nothing can reach.
- At least two variants. One variant is a tool with a pointless argument.

Give every variant a `description`. The enum tells the model the strings; the description is the
only thing telling it what they mean.

## Writing descriptions a model can act on

The description is the entire basis for the model's choice. Write for that reader.

- Say what the tool returns, not only what it does. "Returns file ids and names" beats "lists
  files", because the next tool needs an id.
- Name the tool that produces an argument this one needs: "`fileId` comes from `list_files`."
- State the constraint that will otherwise be violated: page sizes, required filters, opaque ids.
- Do not describe the HTTP mechanics. The model is not issuing the request.
- Set `"readOnly": true` on anything that does not change state. It is advisory, but clients show
  it and some ask before calling a tool without it.

## The three kinds of guidance

Beyond tools, use these deliberately rather than filling all three:

| Field | Use it for | Reaches |
| --- | --- | --- |
| `instructions` | the two or three sentences every session needs: which tool to start with, what ids look like, hard limits | every MCP client |
| `prompts` | a reusable task someone would run repeatedly | clients that list prompts |
| `skills` | a procedure long enough to need headings and steps | clients that read resources |

Prompt message text uses the same `{placeholder}` syntax, resolved against the prompt's declared
`arguments`. A required argument that is missing is an error; an optional one substitutes nothing.

## Limits

| | |
| --- | --- |
| Tools per manifest | 64 |
| Variants per tool | 16 |
| Prompts | 32 |
| Skills | 8 |
| Whole manifest | 128 KB |
| Response carried back per call | 1 MB, then truncated with a note |

Responses come back as text. There is no `outputSchema` and no structured output, so a tool that
returns a 40 MB export is a tool that returns the first megabyte of one — use the API's own field
selection and paging parameters to keep answers small, and say so in the description.

## Before you hand it over

1. Run the validator:
   ```
   dotnet run --project tools/RavensPort.ManifestCheck -- path/to/your-manifest.json
   ```
   It prints every call the manifest would make. Read that list — it is the same list the user sees
   in the app, and it is where a wrong path shows up.
2. Check each path against the API's documentation **with the route's prefix prepended**. The
   validator cannot know the user's route, so this is the one class of error nothing catches for
   you.
3. Confirm no credential appears anywhere in the file.
4. Confirm every tool that changes state is not marked `readOnly`, and that you were asked for
   write access at all. A read-only manifest is the safer default and should be the starting point
   unless the user said otherwise.

## Starting points in this folder

- `sample-task-tracker.json` — a worked example using every feature once. Also the sample the app's
  **Load sample** button inserts.
- `google-drive-readonly.json` — a real, read-only API: variants, paging, field selection, and a
  skill.
