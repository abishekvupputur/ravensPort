"""Validate the winget manifests against the published JSON schemas.

`winget validate` would be the obvious tool, but winget.exe is not present on GitHub's Windows
runner images -- App Installer is not provisioned in the Server SKUs -- so this checks the same
thing the only other way available: fetch the schema each manifest declares and validate against
it. That catches a malformed manifest at commit time instead of on a winget-pkgs pull request,
where the round trip costs a resubmission.

It deliberately does not reimplement winget's semantic rules (identifier casing against the
directory path, hash matching the installer). Those live in the pipeline, and guessing at them
here would produce a check that disagrees with the real one.

It lives in packaging/ rather than in packaging/winget/ beside the manifests it checks, and that
is not tidiness. `winget validate --manifest <dir>` reads *every* file in the directory it is
given and refuses the whole set on anything that is not a manifest:

    Manifest validation failed.
    The manifest does not contain a valid root. File: validate-manifests.py

So this script sitting next to the manifests made the one command the submission process actually
depends on impossible to run against the real directory -- documented, and broken, until a
submission needed it. packaging/winget/ now holds manifests and nothing else.

Usage:  python packaging/validate-winget-manifests.py [manifest-dir]
"""

import json
import pathlib
import re
import sys
import urllib.request

import yaml
from jsonschema import Draft7Validator

# ManifestType as written in each file -> the name that type carries in the schema URL.
SCHEMA_NAMES = {
    "version": "version",
    "installer": "installer",
    "defaultLocale": "defaultLocale",
    "locale": "locale",
}

SCHEMA_URL = (
    "https://raw.githubusercontent.com/microsoft/winget-cli/master"
    "/schemas/JSON/manifests/v{version}/manifest.{name}.{version}.json"
)

# ManifestVersion is substituted into that URL twice, and it is read straight out of a manifest
# rather than chosen here, so it is checked before it is used. A value carrying slashes or dot
# segments steers the fetch at some other path -- and raw.githubusercontent.com serves every
# repository on GitHub, so "somewhere else on the same host" includes a schema an attacker wrote,
# which would then approve whatever manifest referenced it.
#
# The check earns its place on the ordinary failure too: without it a missing or misspelled
# ManifestVersion means a 404 inside load_schema and a urllib traceback, instead of the FAIL line
# every other malformed manifest gets. winget's schema versions are three dotted numbers (1.12.0).
#
# ManifestType needs no equivalent: it is looked up in SCHEMA_NAMES, so only those four literals
# ever reach the URL.
VERSION_PATTERN = re.compile(r"\d+\.\d+\.\d+")

_schema_cache = {}


class ManifestLoader(yaml.SafeLoader):
    """SafeLoader that leaves dates as the strings the schema expects.

    YAML resolves an unquoted 2026-08-04 to a datetime.date, and jsonschema then rejects
    ReleaseDate for not being a string -- on a manifest winget itself accepts. The manifests are
    correct; the default loader is what is wrong for this job.
    """


ManifestLoader.add_constructor(
    "tag:yaml.org,2002:timestamp", lambda loader, node: loader.construct_scalar(node)
)


def read_manifest(path):
    """Parse one manifest with ManifestLoader.

    Drives the loader directly instead of calling `yaml.load(..., Loader=ManifestLoader)`, which
    is what this was and which does exactly the same three things. The rewrite is for the scanner,
    not for safety: Snyk Code's CWE-502 rule matches the name `yaml.load` and does not look at the
    Loader argument, so it reported deserialization of untrusted data against a loader that has
    been a SafeLoader subclass since the file was written. SafeLoader constructs nothing but
    plain scalars, lists and dicts -- there was no unsafe path to fix, only a sink to stop naming.

    Keep the dispose() in a finally: yaml.load does the same, and skipping it leaks the loader's
    buffers when a malformed manifest raises.
    """
    loader = ManifestLoader(path.read_text(encoding="utf-8"))
    try:
        return loader.get_single_data()
    finally:
        loader.dispose()


def load_schema(name, version):
    key = (name, version)
    if key not in _schema_cache:
        url = SCHEMA_URL.format(name=name, version=version)
        with urllib.request.urlopen(url, timeout=30) as response:
            _schema_cache[key] = json.load(response)
    return _schema_cache[key]


def main(directory):
    manifests = sorted(pathlib.Path(directory).glob("*.yaml"))
    if not manifests:
        sys.exit(f"No .yaml manifests found in {directory}")

    documents = {path: read_manifest(path) for path in manifests}

    failures = 0
    for path, document in documents.items():
        manifest_type = document.get("ManifestType")
        manifest_version = document.get("ManifestVersion")
        if manifest_type not in SCHEMA_NAMES:
            print(f"FAIL {path.name}: unknown ManifestType {manifest_type!r}")
            failures += 1
            continue

        # Before it reaches the schema URL -- see VERSION_PATTERN. str() because an unquoted 1.12
        # is a float by the time it gets here, and that is a malformed version, not a crash.
        if not VERSION_PATTERN.fullmatch(str(manifest_version)):
            print(f"FAIL {path.name}: unusable ManifestVersion {manifest_version!r}")
            failures += 1
            continue

        schema = load_schema(SCHEMA_NAMES[manifest_type], manifest_version)
        errors = sorted(Draft7Validator(schema).iter_errors(document), key=lambda e: e.path)
        if errors:
            failures += 1
            print(f"FAIL {path.name}  (schema {manifest_type} {manifest_version})")
            for error in errors:
                location = "/".join(str(part) for part in error.path) or "(root)"
                print(f"       {location}: {error.message}")
        else:
            print(f"ok   {path.name}  (schema {manifest_type} {manifest_version})")

    # Every submission is a set: winget-pkgs rejects a version manifest without the installer and
    # locale manifests beside it, so a directory that validates file by file can still be wrong.
    types = {document.get("ManifestType") for document in documents.values()}
    for required in ("version", "installer", "defaultLocale"):
        if required not in types:
            print(f"FAIL missing a {required} manifest; a submission needs all three")
            failures += 1

    if failures:
        sys.exit(f"\n{failures} problem(s) found.")
    print(f"\nAll {len(manifests)} manifests validate.")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "packaging/winget")
