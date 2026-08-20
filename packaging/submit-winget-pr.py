"""Open the microsoft/winget-pkgs pull request for a released version.

The second half of the winget submission, after update-winget-manifests.py has written the numbers
in. Called by release.yml once the release exists, and runnable by hand for a resubmission:

    python packaging/submit-winget-pr.py --version 4.4.0

Everything goes through the GitHub API rather than a clone, and that is not an optimisation.
winget-pkgs carries a manifest directory of several hundred thousand files; even a blobless,
depth-1 clone pulls a tree that dwarfs anything this needs, on a runner that is going to add three
small files and then be thrown away. The API adds those three files directly.

One commit, built from a tree rather than three successive Contents API writes. A pull request that
opens with "Update: ... (file 1 of 3)" three times over reads like a mistake to whoever reviews it,
and the moderators there review a great many of these.

Authentication is a PAT in GH_TOKEN with `public_repo`, because this pushes to a fork the
repository's own GITHUB_TOKEN has no rights over. See docs/WINGET-SUBMISSION.md.
"""

import argparse
import base64
import json
import pathlib
import re
import subprocess
import sys

UPSTREAM = "microsoft/winget-pkgs"

# manifests/<first letter of publisher, lowercased>/<Publisher>/<Package>/<version>/
MANIFEST_ROOT = "manifests/a/AbishekNarasimhan/RavensPort"


def gh(*args, body=None):
    """One `gh api` call. Fails loudly: a half-finished submission is worse than none."""
    command = ["gh", *args]
    if body is not None:
        command += ["--input", "-"]

    result = subprocess.run(
        command,
        input=json.dumps(body) if body is not None else None,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise SystemExit(f"$ {' '.join(command)}\n{result.stderr.strip()}")

    return json.loads(result.stdout) if result.stdout.strip().startswith(("{", "[")) else result.stdout.strip()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="digits only, e.g. 4.4.0")
    parser.add_argument("--fork", required=True, help="owner/name of your winget-pkgs fork")
    parser.add_argument("--dir", default="packaging/winget")
    parser.add_argument("--repo", default="abishekvupputur/ravensPort", help="this project, for the PR body")
    args = parser.parse_args()

    if not re.fullmatch(r"\d+(\.\d+){0,3}", args.version):
        raise SystemExit(f"--version must be digits and dots, got {args.version!r}")

    manifests = sorted(pathlib.Path(args.dir).glob("*.yaml"))
    if len(manifests) != 3:
        raise SystemExit(f"expected 3 manifests in {args.dir}, found {len(manifests)}")

    # The version in the files decides the directory they go in, rather than --version deciding it
    # twice. If update-winget-manifests.py has not run, this is where that shows up -- as a refusal,
    # not as a pull request adding 4.3.3 manifests that still say 4.3.2 inside.
    for path in manifests:
        found = re.search(r"^PackageVersion:\s*(\S+)", path.read_text(encoding="utf-8"), re.MULTILINE)
        if not found or found.group(1) != args.version:
            raise SystemExit(
                f"{path.name} says PackageVersion {found.group(1) if found else '(none)'}, "
                f"expected {args.version}. Run update-winget-manifests.py first."
            )

    branch = f"RavensPort-{args.version}"
    owner = args.fork.split("/")[0]

    # Already merged. Checked before anything is created, because the open-pull-request check below
    # cannot see this: a merged pull request is not an open one, so without this a rerun would
    # cheerfully open a second submission for a version that is already published -- and versions in
    # winget-pkgs are immutable, so it could only ever be closed again.
    published = subprocess.run(
        ["gh", "api", f"repos/{UPSTREAM}/contents/{MANIFEST_ROOT}/{args.version}"],
        capture_output=True, text=True,
    )
    if published.returncode == 0:
        print(f"{args.version} is already in {UPSTREAM}. Nothing to submit.")
        return 0

    # An open one means a resubmission, and pushing a second would leave two against the same
    # manifest -- which the submission checklist explicitly asks you not to do.
    existing = gh("api", f"repos/{UPSTREAM}/pulls?state=open&head={owner}:{branch}")
    if existing:
        print(f"Already open: {existing[0]['html_url']}")
        return 0

    # Sync the fork before branching. A fork left behind by an earlier release would put this
    # version's commit on a stale master, and the pull request then shows as behind with unrelated
    # commits in it.
    print(f"Syncing {args.fork} master with {UPSTREAM}...")
    gh("api", f"repos/{args.fork}/merge-upstream", "-X", "POST", body={"branch": "master"})

    base_sha = gh("api", f"repos/{args.fork}/git/ref/heads/master")["object"]["sha"]
    base_tree = gh("api", f"repos/{args.fork}/git/commits/{base_sha}")["tree"]["sha"]
    print(f"Base: {base_sha[:8]}")

    # Blobs first, then one tree holding all three, then one commit pointing at it.
    tree_entries = []
    for path in manifests:
        blob = gh(
            "api", f"repos/{args.fork}/git/blobs", "-X", "POST",
            body={
                "content": base64.b64encode(path.read_bytes()).decode("ascii"),
                "encoding": "base64",
            },
        )
        tree_entries.append({
            "path": f"{MANIFEST_ROOT}/{args.version}/{path.name}",
            "mode": "100644",
            "type": "blob",
            "sha": blob["sha"],
        })
        print(f"  {path.name}  blob {blob['sha'][:8]}")

    tree = gh(
        "api", f"repos/{args.fork}/git/trees", "-X", "POST",
        body={"base_tree": base_tree, "tree": tree_entries},
    )
    commit = gh(
        "api", f"repos/{args.fork}/git/commits", "-X", "POST",
        body={
            "message": f"Update: AbishekNarasimhan.RavensPort to {args.version}",
            "tree": tree["sha"],
            "parents": [base_sha],
        },
    )
    print(f"Commit: {commit['sha'][:8]}")

    # Create or update, decided by asking rather than by guessing. PATCH on a ref that does not
    # exist is a 422, not a create -- and the branch genuinely will not exist on a first submission,
    # nor on a later one if the fork's branch was deleted when the previous version merged, which is
    # the ordinary case. force on the update, so a rerun after a failed submission replaces the
    # branch instead of being refused for a non-fast-forward.
    ref_exists = subprocess.run(
        ["gh", "api", f"repos/{args.fork}/git/ref/heads/{branch}"],
        capture_output=True, text=True,
    ).returncode == 0

    if ref_exists:
        gh("api", f"repos/{args.fork}/git/refs/heads/{branch}", "-X", "PATCH",
           body={"sha": commit["sha"], "force": True})
    else:
        gh("api", f"repos/{args.fork}/git/refs", "-X", "POST",
           body={"ref": f"refs/heads/{branch}", "sha": commit["sha"]})
    print(f"Pushed: {branch} ({'updated' if ref_exists else 'created'})")

    installer = next(p for p in manifests if p.name.endswith(".installer.yaml"))
    sha256 = re.search(r"^\s+InstallerSha256:\s*(\S+)", installer.read_text(encoding="utf-8"), re.MULTILINE).group(1)

    body = f"""## Description

Updates `AbishekNarasimhan.RavensPort` to **{args.version}**. Publisher submission, opened
automatically by the release workflow of
[{args.repo}](https://github.com/{args.repo}) once the release was published.

| | |
|---|---|
| Installer | https://github.com/{args.repo}/releases/download/v{args.version}/RavensPort-Setup-{args.version}.exe |
| SHA256 | `{sha256}` |
| Release notes | https://github.com/{args.repo}/releases/tag/v{args.version} |

The hash is the one taken from the artifact that was uploaded, in the same job that built it. It is
not recomputed later: Inno embeds a timestamp and its compression is not bit-reproducible, so the
same commit built twice produces two different hashes, and only the build behind that URL knows the
right one.

## Manifest Checklist

- [x] Checked that there aren't other open pull requests for the same manifest update/change
- [x] This PR only modifies one (1) manifest
- [x] Validated manifest locally — schema-validated against the published 1.12.0 schemas in CI
- [x] Tested manifest locally with `winget install --manifest <path>` — see below
- [x] Manifest conforms to the 1.12 schema

Every release runs an unattended install, launch, and uninstall of this exact installer on a fresh,
discarded CI runner, plus a Microsoft Defender scan, and both gate the release — so this artifact
could not have been published without passing them. They assert what the validation pipeline
asserts: silent install exits 0, the executable and Start menu shortcut exist, the Add or Remove
Programs entry is written with `DisplayVersion` equal to `PackageVersion`, the application stays
running, and the uninstall removes all three.

### `packageInUse`

Worth restating, since it is unusual: RavensPort refuses to install over a running copy of itself,
and the Inno script makes that case exit **7** — a code Setup produces for no other reason.
`ExpectedReturnCodes` maps it to `packageInUse`, so `winget upgrade` tells the user to close the
application rather than printing a bare exit code.
"""

    url = gh(
        "api", f"repos/{UPSTREAM}/pulls", "-X", "POST",
        body={
            "title": f"Update: AbishekNarasimhan.RavensPort to {args.version}",
            "head": f"{args.fork.split('/')[0]}:{branch}",
            "base": "master",
            "body": body,
        },
    )["html_url"]

    print(f"\nPull request: {url}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
