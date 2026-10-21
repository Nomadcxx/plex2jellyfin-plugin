#!/usr/bin/env python3
import hashlib
import json
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

guid = "f4eda3a1-c062-49b3-a958-7cf9ca80c269"
source = Path(__file__).with_name("update-manifest.py")

with tempfile.TemporaryDirectory() as temp:
    work = Path(temp)
    shutil.copy2(source, work / source.name)
    manifest = [{"guid": guid, "versions": [{"version": "1.2.0.0", "targetAbi": "10.11.0.0"}]}]
    (work / "manifest.json").write_text(json.dumps(manifest))
    package = work / "plugin.zip"
    package.write_bytes(b"plugin package")

    subprocess.run([
        sys.executable, str(work / source.name), "1.3.0", "12.0.0.0",
        str(package), "https://example.invalid/plugin.zip", "Jellyfin 12 support",
    ], cwd=work, check=True)

    updated = json.loads((work / "manifest.json").read_text())[0]["versions"]
    assert updated[0]["version"] == "1.3.0.0"
    assert updated[0]["targetAbi"] == "12.0.0.0"
    assert updated[0]["checksum"] == hashlib.md5(package.read_bytes()).hexdigest()
    assert updated[1]["targetAbi"] == "10.11.0.0"
