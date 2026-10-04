#!/usr/bin/env python3
"""Fetch the pinned VS 1.22.7 Combat Overhaul stack; never install into a live profile."""
import hashlib
import json
from pathlib import Path
import urllib.request
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def fetch():
    destination = ROOT / '.artifacts/compat/packages'
    destination.mkdir(parents=True, exist_ok=True)
    manifest = json.loads((ROOT / 'compat/overhaul-modpack.json').read_text())
    for package in manifest['packages']:
        path = destination / package['file']
        if not path.exists():
            temporary = path.with_suffix('.download')
            urllib.request.urlretrieve(package['url'], temporary)
            if hashlib.sha256(temporary.read_bytes()).hexdigest() != package['sha256']:
                temporary.unlink()
                raise ValueError(f"Checksum mismatch: {package['file']}")
            temporary.replace(path)
        if hashlib.sha256(path.read_bytes()).hexdigest() != package['sha256']:
            raise ValueError(f"Checksum mismatch: {package['file']}")
        # References are used only for compilation. External binaries are not bundled in our mod.
        unpacked = ROOT / '.artifacts/compat/references'
        unpacked.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(path) as archive:
            assets = ROOT / '.artifacts/compat/unpacked' / path.stem
            for name in archive.namelist():
                target = (assets / name).resolve()
                if not target.is_relative_to(assets.resolve()):
                    raise ValueError(f"Unsafe archive entry: {name}")
            archive.extractall(assets)
            for name in archive.namelist():
                if '/' not in name and name.endswith('.dll'):
                    (unpacked / name).write_bytes(archive.read(name))
    return destination


if __name__ == '__main__':
    print('Verified Overhaul packages:', fetch())
