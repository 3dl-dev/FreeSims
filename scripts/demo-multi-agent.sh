#!/usr/bin/env bash
# demo-multi-agent.sh — Multi-agent demo: 2 heuristic agents × 2 Sims in one lot.
#
# Architecture:
#   sidecar reads: CMD_FIFO (merged commands from agent0 + agent1)
#   sidecar writes: EVENT_LOG (all JSON events — perceptions, responses, acks)
#   agent0 + agent1 each tail EVENT_LOG, filter by their own SIM_NAME, write commands to CMD_FIFO
#
# No tee fan-out. No deadlock. One sidecar, one log, two agents.
#
# Exit code: 0 = all criteria met, 1 = one or more criteria failed.
#
# Usage:
#   scripts/demo-multi-agent.sh [--sim1-name NAME] [--sim2-name NAME]

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
GAME_BIN="$REPO_ROOT/SimsVille/bin/Debug/net8.0"
SIDECAR_BIN="$REPO_ROOT/sidecar/freesims-sidecar"
AGENT_SCRIPT="$REPO_ROOT/scripts/sim-agent-v2.py"
DISPLAY_NUM="${DISPLAY_NUM:-:99}"
IPC_SOCK="/tmp/freesims-ipc.sock"
OBSERVER_FILE="/tmp/freesims-observer.jsonl"
DEMO_LOG="/tmp/demo-multi-agent.log"
EVENT_LOG="/tmp/demo-events.jsonl"       # sidecar stdout → this file
PERCEPTION_LOG="/tmp/demo-perceptions.jsonl"
SIDECAR_LOG="/tmp/demo-sidecar.log"
GAME_LOG="/tmp/demo-game.log"
AGENT0_LOG="/tmp/demo-agent0.log"
AGENT1_LOG="/tmp/demo-agent1.log"
CMD_FIFO="/tmp/demo-cmd.fifo.$$"

SIM1_NAME=""
SIM2_NAME=""

while [[ $# -gt 0 ]]; do
    case "$1" in
        --sim1-name) SIM1_NAME="$2"; shift 2 ;;
        --sim2-name) SIM2_NAME="$2"; shift 2 ;;
        *) echo "unknown arg: $1" >&2; exit 1 ;;
    esac
done

XVFB_PID=""
GAME_PID=""
SIDECAR_PID=""
AGENT0_PID=""
AGENT1_PID=""

cleanup() {
    echo "" >&2
    echo "[demo] shutting down..." >&2
    { exec 3>&- 3<&-; } 2>/dev/null || true
    for pid in "$AGENT0_PID" "$AGENT1_PID" "$SIDECAR_PID" "$GAME_PID"; do
        [ -n "$pid" ] && kill "$pid" 2>/dev/null || true
    done
    wait 2>/dev/null || true
    rm -f "$CMD_FIFO"
    echo "[demo] cleanup done." >&2
}
trap cleanup EXIT INT TERM

# ---------------------------------------------------------------------------
# Pre-flight
# ---------------------------------------------------------------------------

[ -f "$GAME_BIN/SimsVille" ]  || { echo "[demo] FAIL: $GAME_BIN/SimsVille not found"; exit 1; }
[ -f "$SIDECAR_BIN" ]         || { echo "[demo] FAIL: $SIDECAR_BIN not found"; exit 1; }
[ -f "$AGENT_SCRIPT" ]        || { echo "[demo] FAIL: $AGENT_SCRIPT not found"; exit 1; }
python3 --version >/dev/null 2>&1 || { echo "[demo] FAIL: python3 not on PATH"; exit 1; }

# ---------------------------------------------------------------------------
# Step 1: Xvfb
# ---------------------------------------------------------------------------

if xdpyinfo -display "$DISPLAY_NUM" >/dev/null 2>&1; then
    echo "[demo] Xvfb already running on $DISPLAY_NUM"
else
    echo "[demo] starting Xvfb on $DISPLAY_NUM..."
    Xvfb "$DISPLAY_NUM" -screen 0 1024x768x24 -ac &
    XVFB_PID=$!
    for i in $(seq 1 10); do
        sleep 0.5
        xdpyinfo -display "$DISPLAY_NUM" >/dev/null 2>&1 && break
        [ "$i" -eq 10 ] && { echo "[demo] FAIL: Xvfb did not start"; exit 1; }
    done
    echo "[demo] Xvfb started (pid=$XVFB_PID)"
fi
export DISPLAY="$DISPLAY_NUM"

# ---------------------------------------------------------------------------
# Step 2: Start SimsVille
# ---------------------------------------------------------------------------

# Kill any stale SimsVille or sidecar processes (leftover from previous runs)
pkill -9 -f 'bin/Debug/net8.0/SimsVille' 2>/dev/null || true
pkill -9 -f 'freesims-sidecar' 2>/dev/null || true
# Wait for stale processes to die and release the socket
for _i in $(seq 1 8); do
    pgrep -f 'bin/Debug/net8.0/SimsVille' >/dev/null 2>&1 || break
    sleep 1
done

rm -f "$IPC_SOCK" "$OBSERVER_FILE" "$EVENT_LOG" "$PERCEPTION_LOG" \
      "$AGENT0_LOG" "$AGENT1_LOG"
[ -e "$IPC_SOCK" ] && { echo "[demo] WARNING: could not remove IPC socket $IPC_SOCK"; exit 1; }

echo "[demo] starting SimsVille..."
cd "$GAME_BIN"
env DISPLAY="$DISPLAY_NUM" SDL_AUDIODRIVER=dummy \
    FREESIMS_IPC=1 FREESIMS_IPC_CONTROL_ALL=1 FREESIMS_OBSERVER=1 \
    ./SimsVille >"$GAME_LOG" 2>&1 &
GAME_PID=$!
cd "$REPO_ROOT"
echo "[demo] game pid=$GAME_PID"

echo "[demo] waiting for IPC socket..."
WAITED=0
while [ ! -S "$IPC_SOCK" ]; do
    sleep 1; WAITED=$((WAITED + 1))
    if ! kill -0 "$GAME_PID" 2>/dev/null; then
        echo "[demo] FAIL: game process died"; tail -20 "$GAME_LOG" >&2; exit 1
    fi
    [ "$WAITED" -ge 90 ] && { echo "[demo] FAIL: IPC socket timeout"; exit 1; }
done
echo "[demo] socket ready after ${WAITED}s — waiting 8s for lot + Sims to load..."
sleep 8

# ---------------------------------------------------------------------------
# Step 3: Start sidecar (streaming events to EVENT_LOG)
# ---------------------------------------------------------------------------

mkfifo "$CMD_FIFO"
# Open FIFO read-write (O_RDWR) so this does not block waiting for a reader.
# The sidecar opens the read end after this line without issue.
exec 3<>"$CMD_FIFO"

# Sidecar writes ALL events (perceptions, responses, acks, dialogs) to EVENT_LOG.
# We extract perceptions to PERCEPTION_LOG separately.
"$SIDECAR_BIN" --sock "$IPC_SOCK" < "$CMD_FIFO" 2>"$SIDECAR_LOG" | \
    while IFS= read -r line; do
        echo "$line" >> "$EVENT_LOG"
        case "$line" in
            '{"type":"perception"'*) echo "$line" >> "$PERCEPTION_LOG" ;;
        esac
    done &
SIDECAR_PID=$!
echo "[demo] sidecar pid=$SIDECAR_PID"
sleep 2

# ---------------------------------------------------------------------------
# Step 4: Detect Sim names
# ---------------------------------------------------------------------------

if [ -z "$SIM1_NAME" ] || [ -z "$SIM2_NAME" ]; then
    echo "[demo] waiting for Sim names from perceptions..."
    DETECT_DONE=0
    for i in $(seq 1 25); do
        sleep 1
        if [ -f "$PERCEPTION_LOG" ] && [ -s "$PERCEPTION_LOG" ]; then
            NAMES=$(python3 -c "
import json
names = []
try:
    with open('$PERCEPTION_LOG') as f:
        for line in f:
            try:
                d = json.loads(line.strip())
                n = d.get('name','')
                if n and n not in names:
                    names.append(n)
                if len(names) >= 2:
                    break
            except: pass
except: pass
print('|'.join(names[:2]))
" 2>/dev/null || echo "")
            COUNT=$(echo "$NAMES" | tr '|' '\n' | grep -c '.' 2>/dev/null || echo 0)
            if [ "$COUNT" -ge 2 ]; then DETECT_DONE=1; break; fi
        fi
    done

    if [ "$DETECT_DONE" -eq 1 ] && [ -n "${NAMES:-}" ]; then
        SIM1_NAME=$(echo "$NAMES" | cut -d'|' -f1)
        SIM2_NAME=$(echo "$NAMES" | cut -d'|' -f2)
        echo "[demo] detected: SIM1='$SIM1_NAME' SIM2='$SIM2_NAME'"
    else
        echo "[demo] WARNING: could not detect 2 Sim names — agent1 will observe all Sims"
        SIM1_NAME=$(echo "${NAMES:-}" | cut -d'|' -f1)
        SIM2_NAME=""
    fi
fi

# ---------------------------------------------------------------------------
# Step 5: Start agents
#
# Agents read from EVENT_LOG (tail -f) filtered to their own Sim.
# Agents write commands to CMD_FIFO.
# ---------------------------------------------------------------------------

# Agent wrapper: tail EVENT_LOG, filter JSON lines for this Sim, pipe to agent
agent_wrapper() {
    local sim_name="$1"
    local agent_idx="$2"
    local log_file="$3"
    # tail -f EVENT_LOG and pipe to the agent script
    # The agent filters by SIM_AGENT_NAME inside Python
    # Write agent stdout to fd 3 (CMD_FIFO write end, kept open by exec 3<>).
    # Using >&3 instead of >> "$CMD_FIFO" prevents the FIFO write end from
    # closing between commands, which would deliver EOF to the sidecar's stdin.
    tail -f --retry "$EVENT_LOG" 2>/dev/null | \
        SIM_AGENT_NAME="$sim_name" SIM_AGENT_INDEX="$agent_idx" \
        python3 "$AGENT_SCRIPT" >&3 2>"$log_file"
}

agent_wrapper "$SIM1_NAME" 0 "$AGENT0_LOG" &
AGENT0_PID=$!

agent_wrapper "$SIM2_NAME" 1 "$AGENT1_LOG" &
AGENT1_PID=$!

echo "[demo] agent0 pid=$AGENT0_PID (sim='$SIM1_NAME')"
echo "[demo] agent1 pid=$AGENT1_PID (sim='$SIM2_NAME')"

# ---------------------------------------------------------------------------
# Step 6: Wait
# ---------------------------------------------------------------------------

echo ""
echo "[demo] === RUNNING — waiting up to 150s for agents ==="
echo "[demo] events: $EVENT_LOG   perceptions: $PERCEPTION_LOG"
echo "[demo] agent0: $AGENT0_LOG"
echo "[demo] agent1: $AGENT1_LOG"
echo ""

DEMO_TIMEOUT=200
WAITED=0
while true; do
    sleep 2; WAITED=$((WAITED + 2))
    A0_DONE=0; A1_DONE=0
    kill -0 "$AGENT0_PID" 2>/dev/null || A0_DONE=1
    kill -0 "$AGENT1_PID" 2>/dev/null || A1_DONE=1
    [ "$A0_DONE" -eq 1 ] && [ "$A1_DONE" -eq 1 ] && { echo "[demo] both agents exited"; break; }
    [ "$WAITED" -ge "$DEMO_TIMEOUT" ] && { echo "[demo] demo timeout (${DEMO_TIMEOUT}s)"; break; }
done

sleep 1
kill "$AGENT0_PID" 2>/dev/null || true
kill "$AGENT1_PID" 2>/dev/null || true
wait "$AGENT0_PID" 2>/dev/null || true
wait "$AGENT1_PID" 2>/dev/null || true

# ---------------------------------------------------------------------------
# Step 7: Evaluate criteria
# ---------------------------------------------------------------------------

echo ""
echo "[demo] === EVALUATING GOAL CRITERIA ==="

PASS=0
FAIL=0

check() {
    local label="$1" result="$2" detail="$3"
    if [ "$result" = "pass" ]; then
        echo "[demo] PASS  $label — $detail"
        PASS=$((PASS + 1))
    else
        echo "[demo] FAIL  $label — $detail"
        FAIL=$((FAIL + 1))
    fi
}

# Criterion 1: Each agent bought at least one object
# Note: grep -c exits 1 on zero matches (POSIX). Use `|| true` so set -e doesn't abort
# and the grep stdout (which still contains "0") is captured correctly.
BUY_A0=$({ grep -c "buying:" "$AGENT0_LOG" 2>/dev/null || true; })
BUY_A1=$({ grep -c "buying:" "$AGENT1_LOG" 2>/dev/null || true; })
if [ "$BUY_A0" -ge 1 ] && [ "$BUY_A1" -ge 1 ]; then
    check "1. catalog buy" "pass" "agent0=${BUY_A0}, agent1=${BUY_A1}"
else
    check "1. catalog buy" "fail" "agent0=${BUY_A0}, agent1=${BUY_A1} (need >=1 each)"
fi

# Criterion 2: At least one agent's Sim spent money
FUNDS_RESULT=$(python3 - "$AGENT0_LOG" "$AGENT1_LOG" 2>/dev/null <<'PYEOF'
import re, sys

def parse_log(path):
    init_f, final_f = None, None
    try:
        with open(path) as f:
            for line in f:
                m = re.search(r'initial funds:\s*(\d+)', line, re.IGNORECASE)
                if m: init_f = int(m.group(1))
                m = re.search(r'Final funds:\s*(\d+)', line)
                if m: final_f = int(m.group(1))
    except Exception:
        pass
    return init_f, final_f

i0, f0 = parse_log(sys.argv[1])
i1, f1 = parse_log(sys.argv[2])
d0 = (i0 - f0) if i0 is not None and f0 is not None else None
d1 = (i1 - f1) if i1 is not None and f1 is not None else None
spent = any(d is not None and d > 0 for d in [d0, d1])
result = f"a0=({i0}->{f0} delta={d0}) a1=({i1}->{f1} delta={d1}) spent={spent}"
print(result)
sys.exit(0 if spent else 1)
PYEOF
) && FUNDS_OK=1 || FUNDS_OK=0
if [ "$FUNDS_OK" -eq 1 ]; then
    check "2. funds decreased" "pass" "$FUNDS_RESULT"
else
    check "2. funds decreased" "fail" "$FUNDS_RESULT (buy issued but may not have executed yet)"
fi

# Criterion 3: At least one Sim logged a clock event
CLOCK_A0=$({ grep -c "time is now\|time_of_day\|start time:" "$AGENT0_LOG" 2>/dev/null || true; })
CLOCK_A1=$({ grep -c "time is now\|time_of_day\|start time:" "$AGENT1_LOG" 2>/dev/null || true; })
TOTAL_CLOCK=$((CLOCK_A0 + CLOCK_A1))
if [ "$TOTAL_CLOCK" -ge 1 ]; then
    check "3. clock awareness" "pass" "a0=${CLOCK_A0}, a1=${CLOCK_A1} clock log(s)"
else
    check "3. clock awareness" "fail" "no clock entries in agent logs"
fi

# Criterion 4: Sims observed each other
LOT_A0=$({ grep -c "lot_avatar" "$AGENT0_LOG" 2>/dev/null || true; })
LOT_A1=$({ grep -c "lot_avatar" "$AGENT1_LOG" 2>/dev/null || true; })
if [ "$((LOT_A0 + LOT_A1))" -ge 1 ]; then
    check "4. lot_avatars observed" "pass" "a0=${LOT_A0}, a1=${LOT_A1} observation(s)"
else
    check "4. lot_avatars observed" "fail" "neither agent observed lot_avatars"
fi

# Criterion 5: No crashes
GAME_ALIVE=0; kill -0 "$GAME_PID" 2>/dev/null && GAME_ALIVE=1
SIDECAR_ERR=$({ grep -ci "panic\|fatal error" "$SIDECAR_LOG" 2>/dev/null || true; })
GAME_ERR=$({ grep -ci "unhandled.*exception\|CRASH" "$GAME_LOG" 2>/dev/null || true; })
if [ "$GAME_ERR" -eq 0 ] && [ "$SIDECAR_ERR" -eq 0 ]; then
    check "5. no crashes" "pass" "game_alive=$GAME_ALIVE, sidecar_errors=$SIDECAR_ERR, game_errors=$GAME_ERR"
else
    check "5. no crashes" "fail" "game_alive=$GAME_ALIVE, sidecar_errors=$SIDECAR_ERR, game_errors=$GAME_ERR"
fi

echo ""
echo "[demo] RESULT: $PASS/5 criteria met ($FAIL failed)"
echo ""

# ---------------------------------------------------------------------------
# Step 8: Summary log
# ---------------------------------------------------------------------------

{
    echo "=== FreeSims Multi-Agent Demo Log ==="
    echo "Date: $(date -u '+%Y-%m-%dT%H:%M:%SZ')"
    echo "Repo: $REPO_ROOT"
    echo ""
    echo "--- Configuration ---"
    echo "Sim 1: '$SIM1_NAME'"
    echo "Sim 2: '$SIM2_NAME'"
    echo "Demo timeout: ${DEMO_TIMEOUT}s"
    echo ""
    echo "--- Criteria ---"
    echo "Pass: $PASS / 5"
    echo "Fail: $FAIL / 5"
    echo ""
    echo "--- Agent 0 log ---"
    cat "$AGENT0_LOG" 2>/dev/null || echo "(empty)"
    echo ""
    echo "--- Agent 1 log ---"
    cat "$AGENT1_LOG" 2>/dev/null || echo "(empty)"
    echo ""
    echo "--- Sidecar log (last 50 lines) ---"
    tail -50 "$SIDECAR_LOG" 2>/dev/null || echo "(empty)"
    echo ""
    echo "--- Game log (last 50 lines) ---"
    tail -50 "$GAME_LOG" 2>/dev/null || echo "(empty)"
    echo ""
    echo "--- Event/Perception counts ---"
    echo -n "Events: "; wc -l < "$EVENT_LOG" 2>/dev/null || echo 0
    echo -n "Perceptions: "; wc -l < "$PERCEPTION_LOG" 2>/dev/null || echo 0
} > "$DEMO_LOG"

echo "[demo] log written to $DEMO_LOG"

[ "$FAIL" -eq 0 ] && { echo "[demo] ALL CRITERIA MET"; exit 0; } || { echo "[demo] $FAIL CRITERIA FAILED"; exit 1; }
