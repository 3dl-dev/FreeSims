# Multi-Agent Demo: 2 Agents × 2 Sims

Demonstrates the full FreeSims IPC stack end-to-end: game engine → VMIPCDriver → sidecar → heuristic Python agents.

## Prerequisites

- SimsVille built: `dotnet build SimsVille/SimsVille.csproj`
- Sidecar built: `cd sidecar && go build -o freesims-sidecar .`
- `GameAssets/` symlink at `SimsVille/bin/Debug/net8.0/GameAssets` → `<repo>/GameAssets`
- Xvfb on `:99` (or any display; set `DISPLAY_NUM=:N` to override)
- Python 3.9+

## Quick Start

```bash
# From repo root
scripts/demo-multi-agent.sh
```

The script auto-detects Sim names from the first perceptions. To pin names:

```bash
scripts/demo-multi-agent.sh --sim1-name "Daisy Greenman" --sim2-name "Bob Newbie"
```

## What It Does

1. Ensures Xvfb is running on `:99`.
2. Launches `SimsVille` with `FREESIMS_IPC=1 FREESIMS_IPC_CONTROL_ALL=1 FREESIMS_OBSERVER=1`.
3. Waits for the IPC socket (`/tmp/freesims-ipc.sock`) to appear.
4. Starts `freesims-sidecar`, routing its stdout to both agent FIFOs.
5. Spawns two `sim-agent-v2.py` processes (one per Sim). Both share the same sidecar connection; each filters perceptions by `SIM_AGENT_NAME`.
6. Each agent runs for 10 in-game minutes or 2 real minutes (whichever first).
7. Evaluates 5 goal criteria, prints pass/fail, and writes a full log to `/tmp/demo-multi-agent.log`.

## Agent Behaviour (sim-agent-v2.py)

The agent is a heuristic state machine — no LLM calls, proving the wire protocol works without API cost:

1. **Lock-on**: On the first perception matching `SIM_AGENT_NAME`, lock onto that Sim's `persist_id`.
2. **Catalog query**: Issue `query-catalog` with `request_id=cat-1`. Wait for the `response` frame.
3. **Buy**: On catalog receipt, pick the cheapest item ≤ 200§ and issue a `buy` command near the Sim's current tile.
4. **Interact**: If the action queue is empty and nearby objects exist, issue `interact` with the first available interaction.
5. **Clock awareness**: Log `time_of_day` transitions (dawn/morning/afternoon/evening/night) and hour changes.
6. **Lot-avatar observation**: Log `lot_avatars` from every perception (other Sims on the lot).
7. **Dialog auto-dismiss**: Auto-respond to `dialog` events with the first available button (prefers "Yes").
8. **Chat**: Every 8 ticks emit a mood-based chat message.

Exit conditions: 10 in-game minutes elapsed, or 2 real-time minutes elapsed, or stdin closes.

## Goal Criteria

| # | Criterion | How verified |
|---|-----------|-------------|
| 1 | Each Sim used the catalog to buy ≥1 object | `"buying:"` log line in each agent's stderr |
| 2 | Each Sim's funds decreased | `Initial funds` vs `Final funds` in agent summary |
| 3 | At least one Sim acted differently at night vs day | `time_of_day changed` log in agent stderr |
| 4 | Sims observed each other's motives (lot_avatars) | `lot_avatar` log line in agent stderr |
| 5 | No crashes | Game process alive + no `exception`/`fatal` in sidecar or game logs |

## Output Files

| File | Contents |
|------|----------|
| `/tmp/demo-multi-agent.log` | Full structured log: criteria results + all agent/sidecar/game logs |
| `/tmp/demo-perceptions.jsonl` | Raw perception events (JSONL, one object per line) |
| `/tmp/demo-agent0.log` | Agent 0 stderr (Sim 1 decision trace) |
| `/tmp/demo-agent1.log` | Agent 1 stderr (Sim 2 decision trace) |
| `/tmp/demo-sidecar.log` | Sidecar stderr |
| `/tmp/demo-game.log` | Game stdout/stderr |

## Architecture Diagram

```
SimsVille (game)
  └── VMIPCDriver
        ├── emits tick acks, perceptions, responses, pathfind-failed, dialogs
        └── accepts: interact, chat, buy, goto, query-catalog, dialog-response, ...
              ↑↓ Unix domain socket /tmp/freesims-ipc.sock

freesims-sidecar
  ├── stdin  ← merged JSON commands from agent0 + agent1 (via FIFO)
  └── stdout → fan-out tee → FIFO → agent0 stdin
                             FIFO → agent1 stdin
                             perception log

sim-agent-v2.py (× 2 instances)
  ├── stdin  ← sidecar stdout (all events; agent filters by SIM_AGENT_NAME)
  └── stdout → sidecar stdin FIFO (merged via shell append)
```

## Known Gaps

- **Lot loading**: The demo relies on `CoreGameScreen.cs`'s auto-load-lot hack (countdown timer loads `house1.xml + Daisy`). A second Sim must already be present in the house XML or spawned via `load-sim`. If only one Sim is detected, criterion 4 (lot_avatars) will not be met.
- **Catalog availability**: The catalog is populated lazily from cached OBJD data. On first launch, many items return `price=0` (OBJD not yet cached). The agent skips zero-price items; buy may be deferred to a later tick.
- **Real-time timeout**: The 2-minute real-time timeout is intentionally short for CI. For a full demo, increase `REAL_TIMEOUT` in `sim-agent-v2.py` or `DEMO_TIMEOUT` in `demo-multi-agent.sh`.
- **LLM agents**: `sim-agent-v2.py` is a heuristic agent. The original `sim-agent.py` uses Claude via the Anthropic SDK. Swap in `sim-agent.py` with `SIM_AGENT_MODEL` set for LLM-controlled Sims.

## Reproduction Recipe

```bash
# 1. Build
dotnet build SimsVille/SimsVille.csproj -c Debug
cd sidecar && go build -o freesims-sidecar . && cd ..

# 2. Ensure symlink
ls SimsVille/bin/Debug/net8.0/GameAssets || \
    ln -sf /home/baron/projects/FreeSims/GameAssets SimsVille/bin/Debug/net8.0/GameAssets

# 3. Start Xvfb (skip if already running)
Xvfb :99 -screen 0 1024x768x24 -ac &

# 4. Run the demo
scripts/demo-multi-agent.sh

# 5. Inspect results
cat /tmp/demo-multi-agent.log
tail -f /tmp/demo-agent0.log /tmp/demo-agent1.log
```
