#!/usr/bin/env python3
"""Writes and signs the update manifest of a release.

    python3 update/sign_manifest.py --version 1.1.0 --key key.pem --out dist FILE...

Writes dist/update.json, listing each release file the apps update from
(name, size, SHA-256), and dist/update.json.sig: an ECDSA P-256 signature
over update.json with SHA-256, as raw r||s in base64. The apps check it with
the public key in update/public-key.pem before they install anything. Signing
uses the openssl command; the key never leaves the machine that signs.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import json
import os
import re
import subprocess
import sys

# Which file each kind of app updates from.
KINDS = {
    "windows": re.compile(r"OOT-SW2SE-[0-9.]+-Windows-x64\.zip"),
    "macos": re.compile(r"OOT-SW2SE-[0-9.]+-macOS\.zip"),
    "linux-appimage": re.compile(r"OOT-SW2SE-[0-9.]+-linux-x86_64\.AppImage"),
    "linux-pyz": re.compile(r"OOT-SW2SE-[0-9.]+-linux\.pyz"),
}


def raw_signature(der: bytes) -> bytes:
    """Turns openssl's DER ECDSA signature into raw r||s (32 bytes each)."""
    if len(der) < 8 or der[0] != 0x30 or der[1] != len(der) - 2:
        raise ValueError("not a DER signature")
    out, offset = b"", 2
    for _ in range(2):
        if der[offset] != 0x02:
            raise ValueError("not a DER signature")
        length = der[offset + 1]
        value = der[offset + 2:offset + 2 + length].lstrip(b"\0")
        if len(value) > 32:
            raise ValueError("not a P-256 signature")
        out += value.rjust(32, b"\0")
        offset += 2 + length
    if offset != len(der):
        raise ValueError("not a DER signature")
    return out


def manifest(version: str, files: list[str]) -> bytes:
    assets = {}
    for path in files:
        name = os.path.basename(path)
        kind = next((kind for kind, pattern in KINDS.items() if pattern.fullmatch(name)), None)
        if kind is None:
            continue
        if f"-{version}-" not in name:
            raise SystemExit(f"error: {name} is not version {version}")
        with open(path, "rb") as file:
            data = file.read()
        assets[kind] = {"name": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}
    if not assets:
        raise SystemExit("error: no release files to list")
    return (json.dumps({"format": 1, "version": version, "assets": assets}, indent=2, sort_keys=True) + "\n").encode()


def sign(data: bytes, key: str, openssl: str = "openssl") -> str:
    der = subprocess.run([openssl, "dgst", "-sha256", "-sign", key], input=data, capture_output=True, check=True).stdout
    return base64.b64encode(raw_signature(der)).decode() + "\n"


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("--version", required=True)
    parser.add_argument("--key", required=True, help="the private key (PEM)")
    parser.add_argument("--out", required=True)
    parser.add_argument("files", nargs="+")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", args.version):
        raise SystemExit("error: --version must be x.y.z")
    data = manifest(args.version, args.files)
    os.makedirs(args.out, exist_ok=True)
    with open(os.path.join(args.out, "update.json"), "wb") as file:
        file.write(data)
    with open(os.path.join(args.out, "update.json.sig"), "w") as file:
        file.write(sign(data, args.key))
    print(data.decode(), end="")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
