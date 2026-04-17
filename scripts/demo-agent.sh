#!/usr/bin/env bash
# demo-agent.sh — Launch FreeSims with Claude Code subagent-controlled Sims.
#
# Starts the game with IPC + external control, connects the sidecar,
# and saves perception output for a Claude Code subagent to read and act on.
#
# Usage:
#   scripts/demo-agent.sh
#
# Then in Claude Code:
#   /work reeims-2de   (or just tell Claude to control the Sims)
#
# Kill with Ctrl+C.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_BIN="$REPO_ROOT/SimsVille/bin/Debug/net8.0"
SIDECAR_BIN="$REPO_ROOT/sidecar/freesims-sidecar"
IPC_SOCK="/tmp/freesims-ipc.sock"
DISPLAY="${DISPLAY:-:99}"
CMD_FIFO="/tmp/freesims-cmd.fifo"
PERCEPTION_LOG="/tmp/freesims-perceptions.jsonl"
SIDECAR_LOG="/tmp/freesims-sidecar.log"

GAME_PID=""
SIDECAR_PID=""

cleanup() {
    echo ""
    echo "[demo] shutting down..."
    exec 3>&- 2>/dev/null || true
    [ -n "$SIDECAR_PID" ] && kill "$SIDECAR_PID" 2>/dev/null || true
    [ -n "$GAME_PID" ] && kill "$GAME_PID" 2>/dev/null || true
    wait 2>/dev/null || true
    rm -f "$IPC_SOCK" "$CMD_FIFO" /tmp/freesims-game-demo.log
    echo "[demo] done. Perceptions saved to $PERCEPTION_LOG"
}
trap cleanup EXIT INT TERM

[ -f "$GAME_BIN/SimsVille" ] || { echo "FAIL: game not built"; exit 1; }
[ -f "$SIDECAR_BIN" ] || { echo "FAIL: sidecar not built"; exit 1; }
xdpyinfo -display "$DISPLAY" >/dev/null 2>&1 || { echo "FAIL: no X display at $DISPLAY"; exit 1; }

rm -f "$IPC_SOCK" /tmp/freesims-observer.jsonl "$PERCEPTION_LOG" "$CMD_FIFO"

echo "[demo] starting game (IPC + external control for all Sims)..."
cd "$GAME_BIN"
env DISPLAY="$DISPLAY" SDL_AUDIODRIVER=dummy \
    FREESIMS_IPC=1 FREESIMS_OBSERVER=1 \
    FREESIMS_IPC_CONTROL_ALL=1 \
    ./SimsVille >/tmp/freesims-game-demo.log 2>&1 &
GAME_PID=$!
cd "$REPO_ROOT"

echo "[demo] waiting for game to load..."
WAITED=0
while [ ! -S "$IPC_SOCK" ]; do
    sleep 1; WAITED=$((WAITED + 1))
    [ $WAITED -ge 60 ] && { echo "FAIL: game didn't start"; exit 1; }
done
echo "[demo] game ready (${WAITED}s), waiting for lot..."
sleep 5

# Command FIFO: agents write JSON commands here, sidecar reads them
mkfifo "$CMD_FIFO"

# Sidecar: reads commands from FIFO, writes perceptions + acks to stdout
# Perceptions (JSON lines starting with {) go to the perception log
# The FIFO stays open via fd 3 so sidecar doesn't see EOF
exec 3>"$CMD_FIFO"
"$SIDECAR_BIN" --sock "$IPC_SOCK" < "$CMD_FIFO" 2>"$SIDECAR_LOG" | \
    while IFS= read -r line; do
        case "$line" in
            '{"type":"perception"'*)
                echo "$line" >> "$PERCEPTION_LOG"
                ;;
        esac
    done &
SIDECAR_PID=$!

sleep 2

echo ""
echo "[demo] === RUNNING ==="
echo "[demo] Perceptions: tail -f $PERCEPTION_LOG"
echo "[demo] Send command: echo '{\"type\":\"chat\",\"actor_uid\":28,\"message\":\"hi\"}' > $CMD_FIFO"
echo "[demo] Or use Claude Code subagents to read perceptions and send commands."
echo "[demo] Press Ctrl+C to stop."
echo ""

# Keep running until interrupted
wait $SIDECAR_PID 2>/dev/null || true
