#!/usr/bin/env python3
"""Wrap raw MGFX effect bytes into an XNBd (desktop OpenGL) container
readable by MonoGame 3.8's Content.Load<Effect>().

mgfxc produces a raw MGFX blob (magic 'MGFX'); Content.Load<Effect> expects
the same blob wrapped in the XNB container that MonoGame's content pipeline
normally emits. This script rewrites each input file in place.

Usage:
    python3 scripts/wrap-xnb-effect.py <file1.xnb> [<file2.xnb> ...]

Idempotent: files that already start with 'XNB' are skipped.
"""
import struct
import sys

READER_NAME = (
    "Microsoft.Xna.Framework.Content.EffectReader, MonoGame.Framework, "
    "Version=0.0.0.0, Culture=neutral, PublicKeyToken=null"
)


def write_7bit(n: int) -> bytes:
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        if n:
            out.append(b | 0x80)
        else:
            out.append(b)
            return bytes(out)


def write_str(s: str) -> bytes:
    b = s.encode("utf-8")
    return write_7bit(len(b)) + b


def wrap(mgfx_bytes: bytes) -> bytes:
    type_readers = write_7bit(1) + write_str(READER_NAME) + struct.pack("<i", 0)
    shared_count = write_7bit(0)
    effect_payload = struct.pack("<I", len(mgfx_bytes)) + mgfx_bytes
    primary = write_7bit(1) + effect_payload
    body = type_readers + shared_count + primary
    total = 10 + len(body)
    header = b"XNBd" + bytes([5, 0]) + struct.pack("<I", total)
    return header + body


def main(argv):
    if len(argv) < 2:
        print(__doc__, file=sys.stderr)
        return 2
    for path in argv[1:]:
        with open(path, "rb") as f:
            data = f.read()
        if data.startswith(b"XNB"):
            print(f"skip {path}: already XNB-wrapped")
            continue
        if not data.startswith(b"MGFX"):
            print(f"SKIP {path}: not an MGFX file", file=sys.stderr)
            continue
        wrapped = wrap(data)
        with open(path, "wb") as f:
            f.write(wrapped)
        print(f"wrapped {path}: {len(data)} -> {len(wrapped)} bytes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
