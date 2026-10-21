#!/usr/bin/env python3
"""Prepend a release entry to the repo-level manifest.json.

Usage: update-manifest.py <version> <target-abi> <zip-path> <download-url> <changelog>
Jellyfin verifies the md5 of the downloaded zip against `checksum`.
"""
import hashlib
import json
import sys
from datetime import datetime, timezone

GUID = "f4eda3a1-c062-49b3-a958-7cf9ca80c269"


def main() -> None:
    version, target_abi, zip_path, url, changelog = sys.argv[1:6]
    if version.count(".") == 2:
        version += ".0"

    with open(zip_path, "rb") as f:
        checksum = hashlib.md5(f.read()).hexdigest()

    with open("manifest.json") as f:
        manifest = json.load(f)

    entry = next(pkg for pkg in manifest if pkg["guid"] == GUID)
    versions = [v for v in entry.get("versions", []) if v.get("version") != version]
    versions.insert(0, {
        "version": version,
        "changelog": changelog,
        "targetAbi": target_abi,
        "sourceUrl": url,
        "checksum": checksum,
        "timestamp": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
    })
    entry["versions"] = versions

    with open("manifest.json", "w") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
