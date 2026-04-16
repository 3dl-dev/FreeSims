#!/usr/bin/env bash
# demo-campfire.sh — Launch FreeSims with campfire convention architecture.
#
# Architecture:
#   SimsVille (game)  ←IPC socket→  sidecar (convention server)  ←campfire→  N agents
#
# The sidecar creates freesims.lot, publishes convention declarations, and
# bridges game IPC ↔ campfire. Each agent joins the lot, reads conventions to
# learn the universe, polls perception broadcasts for its own sim, and invokes
# convention operations (walk-to, speak, interact-with) to act.
#
# Usage:
#   scripts/demo-campfire.sh [--duration SECS]
#
# Requires: Xvfb, SimsVille binary, sidecar binary, cf CLI, claude-agent-sdk.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_BIN="$REPO_ROOT/SimsVille/bin/Debug/net8.0"
SIDECAR_BIN="$REPO_ROOT/sidecar/freesims-sidecar"
AGENT_SCRIPT="$REPO_ROOT/scripts/sim-agent-v4.py"
DURATION="${1:-180}"
if [ "$1" = "--duration" ] 2>/dev/null; then DURATION="$2"; fi

DISPLAY_NUM=":99"
IPC_SOCK="/tmp/freesims-ipc.sock"

PIDS=()
cleanup() {
    echo "[demo] shutting down..."
    for pid in "${PIDS[@]}"; do
        kill "$pid" 2>/dev/null || true
    done
    sleep 1
    for pid in "${PIDS[@]}"; do
        kill -9 "$pid" 2>/dev/null || true
    done
    # Kill any orphan claude processes from Agent SDK
    pkill -f 'claude --print' 2>/dev/null || true
    sleep 0.5
    pkill -9 -f 'claude --print' 2>/dev/null || true
    echo "[demo] cleanup done."
}
trap cleanup EXIT INT TERM

# --- 1. Xvfb ---
if ! pgrep -f "Xvfb $DISPLAY_NUM" > /dev/null 2>&1; then
    echo "[demo] starting Xvfb on $DISPLAY_NUM"
    Xvfb "$DISPLAY_NUM" -screen 0 1024x768x24 -ac > /dev/null 2>&1 &
    PIDS+=($!)
    sleep 1
else
    echo "[demo] Xvfb already running on $DISPLAY_NUM"
fi

# --- 2. SimsVille ---
rm -f "$IPC_SOCK"
echo "[demo] starting SimsVille..."
DISPLAY="$DISPLAY_NUM" FREESIMS_IPC=1 FREESIMS_IPC_CONTROL_ALL=1 FREESIMS_OBSERVER=1 \
    "$GAME_BIN/SimsVille" > /tmp/demo-game.log 2>&1 &
GAME_PID=$!
PIDS+=($GAME_PID)
echo "[demo] game pid=$GAME_PID"

# Wait for IPC socket
WAITED=0
while [ ! -S "$IPC_SOCK" ] && [ "$WAITED" -lt 30 ]; do
    sleep 1; WAITED=$((WAITED + 1))
done
if [ ! -S "$IPC_SOCK" ]; then
    echo "[demo] FAIL: IPC socket timeout after ${WAITED}s"
    exit 1
fi
echo "[demo] socket ready after ${WAITED}s — waiting 8s for lot + Sims to load..."
sleep 8

# --- 3. Sidecar (convention server) ---
CF_SIDECAR="/tmp/cf-sidecar-$$"
rm -rf "$CF_SIDECAR" && mkdir -p "$CF_SIDECAR"
cf init --cf-home "$CF_SIDECAR" > /dev/null 2>&1

CF_HOME="$CF_SIDECAR" "$SIDECAR_BIN" --campfire --sock "$IPC_SOCK" \
    < /dev/null > /tmp/demo-sidecar.log 2>&1 &
SIDECAR_PID=$!
PIDS+=($SIDECAR_PID)
sleep 4

# Extract lot campfire ID
LOT=$(grep "FREESIMS_CF_LOT=" /tmp/demo-sidecar.log | head -1 | cut -d= -f2)
if [ -z "$LOT" ]; then
    echo "[demo] FAIL: sidecar did not emit FREESIMS_CF_LOT"
    cat /tmp/demo-sidecar.log >&2
    exit 1
fi
echo "[demo] sidecar pid=$SIDECAR_PID  lot=$LOT"

# --- 4. Discover Sims from perception stream ---
echo "[demo] waiting for perception stream to identify Sims..."
sleep 10
SIMS=$(tail -c 500000 /proc/$SIDECAR_PID/fd/1 2>/dev/null | python3 -c "
import json, sys
seen = {}
for line in sys.stdin:
    line = line.strip()
    if not line.startswith('{\"type\":\"perception'): continue
    try: p = json.loads(line)
    except: continue
    name = p.get('name')
    pid = p.get('persist_id')
    if name and pid and name not in seen:
        seen[name] = pid
for name, pid in sorted(seen.items()):
    print(f'{name}:{pid}')
" 2>/dev/null)

if [ -z "$SIMS" ]; then
    echo "[demo] FAIL: no Sims found in perception stream"
    exit 1
fi

echo "[demo] discovered Sims:"
echo "$SIMS" | while IFS=: read -r name pid; do
    echo "  $name (persist_id=$pid)"
done

# --- 5. Launch one agent per Sim ---
echo "$SIMS" | while IFS=: read -r SIM_NAME SIM_PID; do
    CF_AGENT="/tmp/cf-agent-${SIM_NAME}-$$"
    rm -rf "$CF_AGENT" && mkdir -p "$CF_AGENT"
    cf init --cf-home "$CF_AGENT" > /dev/null 2>&1
    CF_HOME="$CF_AGENT" cf join "$LOT" > /dev/null 2>&1

    rm -f "/tmp/embodied-agent-${SIM_PID}.jsonl"
    CF_HOME="$CF_AGENT" SIM_NAME="$SIM_NAME" SIM_PERSIST_ID="$SIM_PID" FREESIMS_CF_LOT="$LOT" \
        python3 "$AGENT_SCRIPT" > "/tmp/demo-agent-${SIM_NAME}.log" 2>&1 &
    AGENT_PID=$!
    PIDS+=($AGENT_PID)
    echo "[demo] agent $SIM_NAME pid=$AGENT_PID (persist_id=$SIM_PID)"
done

# --- 6. Run for duration ---
echo ""
echo "[demo] === RUNNING for ${DURATION}s ==="
echo "[demo] sidecar log: /tmp/demo-sidecar.log"
echo "[demo] game log:    /tmp/demo-game.log"
echo "$SIMS" | while IFS=: read -r name pid; do
    echo "[demo] agent $name:  /tmp/demo-agent-${name}.log  embodied: /tmp/embodied-agent-${pid}.jsonl"
done
echo ""

sleep "$DURATION"

# --- 7. Report ---
echo ""
echo "[demo] === RESULTS ==="
echo "$SIMS" | while IFS=: read -r name pid; do
    LOG="/tmp/embodied-agent-${pid}.jsonl"
    if [ -f "$LOG" ]; then
        EVENTS=$(wc -l < "$LOG")
        INTENTS=$(grep -c '"event": "intent"' "$LOG" 2>/dev/null || echo 0)
        FULFILLS=$(grep -c '"event": "fulfillment"' "$LOG" 2>/dev/null || echo 0)
        echo "[demo] $name: $EVENTS events, $INTENTS intents, $FULFILLS fulfillments"
    else
        echo "[demo] $name: NO LOG FILE"
    fi
done
