#!/usr/bin/env bash
# Extract The Sims 1 Complete Collection game assets from an install iso
# into ./GameAssets/TheSims/ in the layout FreeSims expects.
#
# Idempotent: rerunning with the same iso is a no-op if the manifest matches.
# Repeatable: manifest.sha256 pins every file so anyone with the same iso
# reproduces the same tree.
#
# Requires: 7z (p7zip-full), unshield.
# Assets are NEVER committed — .gitignore covers ./GameAssets/ and ./.assets-src/.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ISO="${1:-${ROOT}/.assets-src/sims-cc.iso}"
OUT="${ROOT}/GameAssets/TheSims"
STAGE="${ROOT}/.assets-src/stage"
MANIFEST="${ROOT}/GameAssets/manifest.sha256"

# Known-good sentinel proving the extraction worked. This file exists in
# GameData_ALL_Recursive and is the validation file LinuxLocator checks for.
SENTINEL_RELPATH="GameData/Behavior.iff"

log() { printf '[extract-assets] %s\n' "$*"; }
die() { printf '[extract-assets] ERROR: %s\n' "$*" >&2; exit 1; }

[ -f "$ISO" ] || die "iso not found: $ISO (pass path as first arg, or place at .assets-src/sims-cc.iso)"
command -v 7z       >/dev/null || die "7z not found — apt install p7zip-full"
command -v unshield >/dev/null || die "unshield not found — apt install unshield"

# Short-circuit if already extracted and sentinel exists
if [ -f "$OUT/$SENTINEL_RELPATH" ] && [ -f "$MANIFEST" ]; then
  log "already extracted at $OUT (sentinel + manifest present); verifying..."
  if (cd "$(dirname "$OUT")" && sha256sum --quiet -c "$MANIFEST"); then
    log "manifest verified. No work to do."
    exit 0
  fi
  log "manifest mismatch — re-extracting"
fi

# 1. Unpack iso Setup/ directory using 7z
log "unpacking iso -> $STAGE"
rm -rf "$STAGE"
mkdir -p "$STAGE"
7z x -y -o"$STAGE" "$ISO" 'Setup/*' > /dev/null
[ -f "$STAGE/Setup/data1.hdr" ] || die "Setup/data1.hdr missing after 7z extraction — wrong iso?"

# 2. Run unshield against the CAB files
RAW="$STAGE/raw"
rm -rf "$RAW"
mkdir -p "$RAW"
log "unpacking InstallShield CABs -> $RAW"
(cd "$STAGE/Setup" && unshield -d "$RAW" x data1.hdr > /dev/null)

# 3. Compose the TheSims/ layout the game engine expects.
#    Unshield file groups map to directories; we merge a few.
log "composing game directory -> $OUT"
rm -rf "$OUT"
mkdir -p "$OUT"

copy_group() {
  local src="$RAW/$1" dst="$OUT/$2"
  [ -d "$src" ] || { log "  skip: group $1 missing in CAB (harmless for partial isos)"; return 0; }
  mkdir -p "$dst"
  cp -r "$src/." "$dst/"
}

# GameData merges two groups
copy_group "GameData_ALL_Recursive" "GameData"
copy_group "GameData_ALL_Ranger"    "GameData"

# UserData merges base + extra
copy_group "UserData"  "UserData"
copy_group "UserData2" "UserData"

# UI, sound, expansion, templates, music
copy_group "UIGraphics"          "UIGraphics"
copy_group "SoundData_SimsSounds" "SoundData"
copy_group "ExpansionShared"     "ExpansionShared"
copy_group "Music_ALL_Recursive" "Music"
for i in 1 2 3 4 5 6 7 GOLD; do
  copy_group "ExpansionPack${i}" "ExpansionPack${i}"
done
copy_group "TemplateCommunity"       "TemplateCommunity"
copy_group "TemplateDowntown"        "TemplateDowntown"
copy_group "TemplateFamilyUnleashed" "TemplateFamilyUnleashed"
copy_group "TemplateMagicTown"       "TemplateMagicTown"
copy_group "TemplateNPCs"            "TemplateNPCs"
copy_group "TemplateStudiotown"      "TemplateStudiotown"
copy_group "TemplateUserData"        "TemplateUserData"
copy_group "TemplateVacation"        "TemplateVacation"
copy_group "UserData_WT_LotGFX"      "UserData_WT_LotGFX"
copy_group "UserData_WT_LotGFX_MM"   "UserData_WT_LotGFX_MM"
copy_group "Creator_Files"           "Creator_Files"

# Main exe (not needed to run, but FreeSims auto-detection may reference it)
[ -f "$RAW/SIMS_EXE/Sims.exe" ] && cp "$RAW/SIMS_EXE/Sims.exe" "$OUT/Sims.exe"

# 4. Validate sentinel
[ -f "$OUT/$SENTINEL_RELPATH" ] || die "sentinel file $SENTINEL_RELPATH missing after extraction — wrong iso or CAB layout changed?"

# 5. Manifest
log "writing manifest -> $MANIFEST"
(cd "$(dirname "$OUT")" && find "$(basename "$OUT")" -type f -print0 | sort -z | xargs -0 sha256sum) > "$MANIFEST"

# 6. Cleanup raw stage (keeps iso; drops intermediate CAB unpacking ~3GB)
log "cleaning up stage directory"
rm -rf "$STAGE"

log "done. $OUT is populated."
log "   files: $(wc -l < "$MANIFEST")"
log "   size:  $(du -sh "$OUT" | cut -f1)"
