"""Rewrite the winget manifests in packaging/winget/ for a released version.

Called by release.yml once the installer has been built, scanned, install-tested and hashed, so
every value written here describes an artifact that already exists and already passed its gates.
Runnable by hand for the same reason it is a script and not ten lines of inline YAML in the
workflow: a submission that has to be repeated should not have to be repeated differently.

    python packaging/update-winget-manifests.py --version 4.3.4 --sha256 E952E9...

Only the fields that move between releases are touched -- version, installer URL, hash, release
date, release-notes link. Everything else in those files is hand-written prose explaining why a
value is what it is (ProductCode, ExpectedReturnCodes, Scope, MinimumOSVersion), and it has to
survive: winget-pkgs keeps the comments in what it merges, and they are the only place the
reasoning is recorded. So this edits lines in place with anchored regexes rather than loading and
re-emitting YAML, which would drop every comment in the file.

The hash is a required argument on purpose. It cannot be recomputed later: Inno embeds a timestamp
and its compression is not bit-reproducible, so the same commit built twice gives two different
hashes, and only the build that produced the uploaded asset knows the right one. Passing it in is
what keeps this from being a guess. See docs/WINGET-SUBMISSION.md.
"""

import argparse
import datetime
import pathlib
import re
import sys

# One entry per field that moves, as (filename glob, key, indent, value name). Anchored on the key
# at a known indent so a value can never be matched inside the prose around it: PackageVersion and
# the two URLs sit at column 0, the installer fields are indented under the Installers list item.
SUBSTITUTIONS = [
    ("*.yaml", "PackageVersion", "", "version"),
    ("*.installer.yaml", "ReleaseDate", "", "date"),
    ("*.installer.yaml", "InstallerUrl", "  ", "url"),
    ("*.installer.yaml", "InstallerSha256", "  ", "sha256"),
    ("*.locale.*.yaml", "ReleaseNotesUrl", "", "notes"),
]


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--version", required=True, help="digits only, e.g. 4.3.4")
    parser.add_argument("--sha256", required=True, help="SHA256 of the published installer asset")
    parser.add_argument("--repo", default="abishekvupputur/ravensPort")
    parser.add_argument("--dir", default="packaging/winget")
    parser.add_argument(
        "--date",
        default=datetime.date.today().isoformat(),
        help="ReleaseDate, ISO-8601. Defaults to today (UTC on a runner).",
    )
    args = parser.parse_args()

    if not re.fullmatch(r"\d+(\.\d+){0,3}", args.version):
        return f"--version must be digits and dots, got {args.version!r}"

    # Upper-cased rather than accepted as given: winget's own tooling writes it upper, the merged
    # 4.3.1 and 4.3.2 manifests are upper, and a case flip would show up as a diff on a line that
    # did not really change.
    sha = args.sha256.strip().upper()
    if not re.fullmatch(r"[0-9A-F]{64}", sha):
        return f"--sha256 must be 64 hex characters, got {args.sha256!r}"

    values = {
        "version": args.version,
        "date": args.date,
        "sha256": sha,
        "url": f"https://github.com/{args.repo}/releases/download/"
               f"v{args.version}/RavensPort-Setup-{args.version}.exe",
        "notes": f"https://github.com/{args.repo}/releases/tag/v{args.version}",
    }

    directory = pathlib.Path(args.dir)
    if not directory.is_dir():
        return f"not a directory: {directory}"

    changed = 0
    for glob, key, indent, value_name in SUBSTITUTIONS:
        line = f"{indent}{key}: {values[value_name]}"
        pattern = rf"^{indent}{re.escape(key)}:.*$"

        for path in sorted(directory.glob(glob)):
            original = path.read_text(encoding="utf-8")
            # A plain function as the replacement, so nothing in a URL or a hash is ever read as a
            # backreference: re.sub treats \1 and \g<name> in a replacement *string* as syntax.
            updated, count = re.subn(pattern, lambda _: line, original, flags=re.MULTILINE)

            # A field that matched nothing is the failure worth catching: it means a manifest was
            # restructured and this script quietly stopped maintaining one of its values, which
            # would reach winget-pkgs as a version pointing at the previous release's installer.
            if count == 0:
                return f"{path.name}: no {key!r} line at indent {len(indent)} -- has the manifest changed shape?"

            if updated != original:
                path.write_text(updated, encoding="utf-8", newline="\n")
                changed += 1
                print(f"  {path.name}: {line.strip()}")

    print(f"\n{changed} file write(s) for {args.version}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
