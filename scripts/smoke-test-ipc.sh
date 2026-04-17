#!/usr/bin/env bash
# smoke-test-ipc.sh — End-to-end test: sidecar stdin → IPC socket → game VM
#
# Proves the VMIPCDriver accepts commands from the sidecar and executes them.
# Verifies: game boots with IPC, sidecar connects, command is delivered and
# acknowledged, and the observer captures Sim state.
#
# Usage: scripts/smoke-test-ipc.sh
# Exit code 0 = pass, non-zero = fail.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_BIN="$REPO_ROOT/SimsVille/bin/Debug/net8.0"
SIDECAR_BIN="$REPO_ROOT/sidecar/freesims-sidecar"
OQ="$REPO_ROOT/scripts/observer-query.py"
OBSERVER_FILE="/tmp/freesims-observer.jsonl"
IPC_SOCK="/tmp/freesims-ipc.sock"
DISPLAY="${DISPLAY:-:99}"
SIDECAR_FIFO="/tmp/freesims-sidecar-stdin.$$"
SIDECAR_OUT="/tmp/freesims-sidecar-out.$$"

GAME_PID=""
SIDECAR_PID=""

cleanup() {
    echo "[smoke] cleaning up..."
    exec 3>&- 2>/dev/null || true
    [ -n "$SIDECAR_PID" ] && kill "$SIDECAR_PID" 2>/dev/null || true
    [ -n "$GAME_PID" ] && kill "$GAME_PID" 2>/dev/null || true
    [ -n "$GAME_PID" ] && wait "$GAME_PID" 2>/dev/null || true
    [ -n "$SIDECAR_PID" ] && wait "$SIDECAR_PID" 2>/dev/null || true
    rm -f "$IPC_SOCK" "$OBSERVER_FILE" "$SIDECAR_FIFO" "$SIDECAR_OUT" "${GAME_LOG:-}"
    echo "[smoke] done."
}
trap cleanup EXIT

[ -f "$GAME_BIN/SimsVille" ] || { echo "[smoke] FAIL: game not built"; exit 1; }
[ -f "$SIDECAR_BIN" ] || { echo "[smoke] FAIL: sidecar not built"; exit 1; }
xdpyinfo -display "$DISPLAY" >/dev/null 2>&1 || { echo "[smoke] FAIL: no X display at $DISPLAY"; exit 1; }

rm -f "$IPC_SOCK" "$OBSERVER_FILE"

echo "[smoke] starting game with FREESIMS_IPC=1 FREESIMS_OBSERVER=1..."
cd "$GAME_BIN"
GAME_LOG="/tmp/freesims-game.$$.log"
env DISPLAY="$DISPLAY" SDL_AUDIODRIVER=dummy FREESIMS_IPC=1 FREESIMS_OBSERVER=1 ./SimsVille >"$GAME_LOG" 2>&1 &
GAME_PID=$!
cd "$REPO_ROOT"

echo "[smoke] waiting for IPC socket..."
WAITED=0
while [ ! -S "$IPC_SOCK" ]; do
    sleep 1; WAITED=$((WAITED + 1))
    [ $WAITED -ge 60 ] && { echo "[smoke] FAIL: IPC socket timeout"; exit 1; }
done
echo "[smoke] TEST 1 PASS: IPC socket created (after ${WAITED}s)"

sleep 5

mkfifo "$SIDECAR_FIFO"
"$SIDECAR_BIN" --sock "$IPC_SOCK" < "$SIDECAR_FIFO" > "$SIDECAR_OUT" 2>&1 &
SIDECAR_PID=$!
exec 3>"$SIDECAR_FIFO"
sleep 2

# Verify sidecar connected
if grep -q "connected" "$SIDECAR_OUT" 2>/dev/null; then
    echo "[smoke] TEST 2 PASS: sidecar connected to IPC socket"
else
    echo "[smoke] FAIL: sidecar did not connect"
    cat "$SIDECAR_OUT"
    exit 1
fi

# Verify observer is producing avatar data
echo "[smoke] waiting for observer data..."
WAITED=0
ACTOR_UID=""
while [ -z "$ACTOR_UID" ]; do
    sleep 2; WAITED=$((WAITED + 2))
    [ $WAITED -ge 60 ] && { echo "[smoke] FAIL: no avatar data in observer"; exit 1; }
    [ -f "$OBSERVER_FILE" ] && [ -s "$OBSERVER_FILE" ] && \
        ACTOR_UID=$(python3 "$OQ" first-sim "$OBSERVER_FILE" 2>/dev/null) || true
done
echo "[smoke] TEST 3 PASS: observer producing Sim data (persist_id=$ACTOR_UID, after ${WAITED}s)"

# Send a chat command (verifies command delivery + execution)
echo "[smoke] sending chat command..."
echo "{\"type\":\"chat\",\"actor_uid\":$ACTOR_UID,\"message\":\"IPC smoke test\"}" >&3
sleep 5

# Check ack stream for command_count > 0
if grep -q '"command_count":1' "$SIDECAR_OUT" 2>/dev/null; then
    echo "[smoke] TEST 4 PASS: command delivered and acknowledged by game"
else
    echo "[smoke] FAIL: no command acknowledgment found"
    tail -5 "$SIDECAR_OUT"
    exit 1
fi

exec 3>&-
rm -f "$SIDECAR_FIFO"

echo ""
echo "[smoke] ALL TESTS PASSED"
echo "[smoke] IPC socket + sidecar + observer + command delivery verified"
