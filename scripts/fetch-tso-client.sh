#!/usr/bin/env bash
# Fetch and unpack the original TSO (The Sims Online) client assets into
# ./GameAssets/TSOClient/ so SimsVille can boot.
#
# Why: SimsVille is a fork of FreeSO (TSO reimplementation) with a TS1 content
# loader bolted on. It boots against a TSO asset tree (UI, avatars, objects,
# globals) and then loads TS1 lot content on top. The Sims 1 disc alone is not
# enough. EA open-sourced the TSO client assets; a donated copy sits at
# archive.org item TheSimsOnline_201802 under CC-BY-ND.
#
# Idempotent: rerunning with valid extraction is a no-op.
# Requires: curl, 7z (p7zip-full). Assets are never committed.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ARCHIVE_URL="${TSO_ARCHIVE_URL:-https://archive.org/download/TheSimsOnline_201802/TSO.zip}"
ZIP="${ROOT}/.assets-src/tso.zip"
STAGE="${ROOT}/.assets-src/tso-stage"
OUT="${ROOT}/GameAssets/TSOClient"
SENTINEL="$OUT/objectdata/objects/objiff.far"

log() { printf '[fetch-tso-client] %s\n' "$*"; }
die() { printf '[fetch-tso-client] ERROR: %s\n' "$*" >&2; exit 1; }

command -v curl >/dev/null || die "curl not found"
command -v 7z   >/dev/null || die "7z not found — apt install p7zip-full"

if [ -f "$SENTINEL" ]; then
  log "already extracted at $OUT (sentinel objiff.far present). Done."
  exit 0
fi

mkdir -p "$(dirname "$ZIP")"

if [ ! -f "$ZIP" ] || [ "$(stat -c %s "$ZIP" 2>/dev/null || echo 0)" -lt 1000000000 ]; then
  log "downloading TSO bundle (~1.2 GB) from $ARCHIVE_URL"
  curl -L --connect-timeout 30 -o "$ZIP" "$ARCHIVE_URL"
fi

[ -f "$ZIP" ] || die "download failed"
log "unpacking zip -> $STAGE"
rm -rf "$STAGE"
mkdir -p "$STAGE"
7z x -y -o"$STAGE" "$ZIP" > /dev/null
[ -f "$STAGE/Data1.cab" ] || die "Data1.cab missing after unzip — unexpected bundle layout"

log "extracting multi-volume CAB set (this is the slow part)"
rm -rf "$OUT"
mkdir -p "$(dirname "$OUT")"
local_raw="$(mktemp -d -t tso-raw.XXXXXX)"
7z x -y -o"$local_raw" "$STAGE/Data1.cab" > /dev/null
[ -d "$local_raw/TSOClient" ] || die "TSOClient directory missing after CAB extraction"

mv "$local_raw/TSOClient" "$OUT"
rmdir "$local_raw" 2>/dev/null || true

[ -f "$SENTINEL" ] || die "sentinel $SENTINEL missing after extraction — wrong bundle?"

log "cleaning stage dir"
rm -rf "$STAGE"

log "done. $OUT is populated."
log "   size:  $(du -sh "$OUT" | cut -f1)"
log "   sentinel: $SENTINEL"
