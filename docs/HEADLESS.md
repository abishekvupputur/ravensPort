# Running RavensPort headless

RavensPort is one program with two ways in. Launched plainly, it is the desktop app. Given a
command, it is a command line, and no window is opened:

- **`ravensport serve`** runs the proxy, MCP funnels and API bridges with no window, on a machine
  with no display: a server, a Raspberry Pi, a VM.
- **Every other command** manages whichever RavensPort is running on this machine, using the vault
  it has already unlocked. That can be `serve`, or the desktop app you are sitting in front of:
  unlock the vault in the window once, and `ravensport routes list` works from any terminal without
  asking for anything again. Changes made from the terminal show up in the window straight away.

On Linux it is `/usr/bin/ravensport`, from the Debian/Ubuntu package. On Windows it is
`RavensPort.exe` from the installer; tick **Add RavensPort to PATH** during setup to run it from any
terminal (the command is not case-sensitive there).

## Where the configuration comes from

Exactly as in the desktop app: from a vault in your password manager. You can set everything up in
the desktop app on your own machine, and a headless server pointed at the same vault serves it.

The secret that opens the vault is read from **stdin**, and only from stdin. It is never accepted
as an argument (a command line is visible to every process on the machine and ends up in shell
history) and never read from an environment variable. It is not written anywhere.

| Backend | Secret | Mode |
|---|---|---|
| 1Password (default) | A **service account token** (`ops_…`), granted access to the RavensPort vault | Read-write, like the desktop app |
| Proton Pass (`--backend protonpass`) | A **personal access token** (`pst_…`), from `pass-cli pat create`, granted the RavensPort vault | **Read-only** |

### Proton Pass is read-only

A Proton Pass session reads the configuration from the vault and never writes back. You can still
make changes with `ravensport`, and they take effect immediately, but they are held in memory
and **discarded when `serve` stops**. Every command that changes something says so, and
`ravensport status` shows how much is unsaved.

Personal-access-token sessions last two hours. RavensPort keeps the token in memory so that it can
log in again before its next vault read. It uses its own temporary session folder, which is deleted
on exit, and it never touches the desktop app's Proton Pass session. `pass-cli` must be installed in
a system location (`/usr/bin` or `/usr/local/bin` on Linux).

**Refreshed OAuth tokens are not saved either.** For most providers that does not matter. Some
providers, though, issue a new refresh token on every refresh and invalidate the old one. For those,
the copy in the vault stops working once the headless server has refreshed it, so the credential
has to be signed in again after a restart, and in any desktop app that shares the vault.
Device-code, client-credentials and service-account credentials are not affected.

## Starting it

```bash
# 1Password, with the token piped in
systemd-creds cat op-token | ravensport serve

# or typed at a prompt that does not echo
ravensport serve

# Proton Pass, read-only
ravensport serve --backend protonpass < ~/.config/ravensport/pass-pat
```

On Windows:

```powershell
Get-Content $env:USERPROFILE\op-token.txt | RavensPort serve
RavensPort status | Out-Host
```

RavensPort.exe is a windowed program, so that the Start menu never flashes a console, and given a
command it borrows the terminal it was started from. One consequence: cmd and PowerShell do not
wait for a windowed program unless its output is piped or redirected, so a bare `RavensPort status`
can print after the prompt has already come back. Piping makes the shell wait: pipe the token into
`serve`, and add `| Out-Host` to other commands (or use `start /wait RavensPort …` in cmd).

Options:

| Option | |
|---|---|
| `--backend onepassword\|protonpass` | Which password manager to read from |
| `--vault <name>` | 1Password: the vault to use when none holds a RavensPort configuration yet |
| `--create-vault <name>` | 1Password: create a new vault and use it |
| `--read-only` | Never write to the vault, even with 1Password |

The proxy listens on `127.0.0.1` only, on the port stored in the vault, exactly as the desktop app
does. Reach it from other machines through an SSH tunnel or a reverse proxy of your own. Only one
RavensPort runs at a time: `serve` refuses to start while the desktop app is running, and the
other way round.

`Ctrl+C` or `SIGTERM` stops it, after pending changes have been written to the vault.

### As a systemd service

The package ships two examples in `/usr/share/doc/ravensport/examples/`. Neither is enabled. Each
file explains its own setup. In short:

```bash
sudo useradd --system --create-home --home-dir /var/lib/ravensport ravensport
sudo install -d -m 0700 /etc/ravensport
sudo systemd-creds encrypt --name=op-token - /etc/ravensport/op-token.cred   # paste, then Ctrl+D
sudo cp /usr/share/doc/ravensport/examples/ravensport-headless.service /etc/systemd/system/
sudo systemctl daemon-reload && sudo systemctl enable --now ravensport-headless
```

systemd decrypts the token into a private credentials directory and feeds it to `serve` on stdin.
It is encrypted at rest with the machine's TPM or host key.

## Managing it

Every other command talks to the running RavensPort through its **admin socket**, a Unix domain
socket that only your user can open:

- Linux: `$XDG_RUNTIME_DIR/ravensport/admin.sock`
- Windows: `%LOCALAPPDATA%\RavensPort\admin.sock`

No proxy key is needed; the operating system has already decided who may connect. Point at another
socket with `--socket` (the example units use `/run/ravensport/admin.sock`, so run commands as the
service user: `sudo -u ravensport ravensport --socket /run/ravensport/admin.sock status`).

Management goes through the running process, rather than opening the vault itself, because two
processes writing one vault would corrupt its index. The desktop app answers on the same socket,
and its tabs update when a command changes something.

Add `--json` to any command for machine-readable output.

```bash
ravensport status
ravensport reload [--force]                   # re-read the vault after edits made elsewhere

ravensport credentials list
ravensport credentials add github --kind device-code --preset github --client-id Iv1.abc --scopes "repo read:org"
ravensport credentials signin github          # prints the device code and waits
echo "$KEY" | ravensport credentials add weather --kind api-key
ravensport credentials test github
ravensport credentials remove github

ravensport upstreams add github-api https://api.github.com
ravensport routes add /github --upstream github-api --credential github [--key-days 30]
ravensport routes key show /github            # the route's proxy key, for your client
ravensport routes key rotate /github
ravensport routes disable /github

ravensport sources add "GitHub MCP" --url https://api.githubcopilot.com/mcp/
ravensport sources refresh
ravensport funnels add "Coding agent"
ravensport funnels source add coding-agent githubmcp --only search_code get_file_contents
ravensport funnels key show coding-agent

ravensport bridges operations openapi.yaml
ravensport bridges import Weather openapi.yaml --openapi --route /weather --operation "GET /forecast"

ravensport settings get
ravensport settings set funnel on
ravensport settings mtls generate --output client.pfx   # password from stdin
```

Secrets for `credentials add` (an API key, a client secret, a token-exchange key) are read from
stdin, or asked for without echo. Use `--with-client-secret` when an OAuth2 or device-code client
has one. A Google service account key is given as a file with `--service-account-file`.

### Signing in without a browser

**Device code is the easy path.** `credentials signin` prints a code and a URL; open the URL on any
device, enter the code, and the command finishes when the provider approves it.

**Browser OAuth2 also works**, but the provider redirects to a fixed loopback port on the machine
running RavensPort (`127.0.0.1:51005`, or Google's own port). `credentials signin` prints the
authorization URL and the port. From another machine, forward the port first:

```bash
ssh -L 51005:127.0.0.1:51005 your-server
```

then open the URL in your local browser.

## Exit codes

| Code | Meaning |
|---|---|
| 0 | Done |
| 1 | Refused (the reason is printed) |
| 2 | Wrong usage, or no secret on stdin |
| 3 | No RavensPort is running to manage |
| 4 | The vault could not be opened |
| 5 | `serve` could not start: another instance, or the port is in use |
