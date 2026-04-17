---
date: 2026-04-17
duration_s: 240
expected_sims: 2
model: claude-opus-4-7 (agent SDK default — same as orchestrator)
commit: 1eaedde (Rebuild 8 shader .xnb files to MGFX v10)
---

# FreeSims Playtest — 2026-04-17T14:52:15Z

First playtest on a fresh dotnet build after restoring git, committing 10 commits' worth of working-tree state, and rebuilding all 8 MGFX v8 shaders to v10. Also first playtest after fixing the `colorpoly2d`/`colorpoly2D` case mismatch in `Neighborhood.cs:97`.

## Verdict: **PASS (with caveats)**

Both target Sims embodied, exercised 4 distinct convention ops between them, achieved social interaction (Daisy tried to converse), hit >80% intent success on non-wait operations. No agent crashes. Engine stayed up the whole run.

## Sims

| Name  | PersistID   | Events | Intents | Non-wait Intents | Fulfillments | Success | Errors | Ops Used |
|-------|-------------|--------|---------|------------------|--------------|---------|--------|----------|
| Daisy | 1795717786  | 85     | 28      | 27               | 25           | **92%** | 1      | `walk-to` (22), `speak` (2), `interact-with` (3), `wait` (1) |
| Gerry | 28          | 93     | 35      | 22               | 18           | **81%** | 3      | `interact-with` (19), `wait` (16) |

Run timeline: `2026-04-17T14:57:11Z → 15:02:27Z` (5 min 16 s wall-clock from first event to last).

## Metrics vs Skill Targets

| Metric                | Value              | Target  | Verdict |
|-----------------------|--------------------|---------|---------|
| `sims_discovered`     | 2 / 2              | == 2    | ✅ PASS |
| `agent_crashes`       | 0                  | == 0    | ✅ PASS |
| `intent_success`      | 88% combined       | > 80%   | ✅ PASS |
| `actions_per_sim`     | 28 (Daisy), 35 (Gerry) | > 5 | ✅ PASS |
| `conventions_used`    | 4 distinct ops     | > 3     | ✅ PASS |
| `social_interactions` | 2 `speak` attempts | > 0     | ✅ PASS |
| `perception_latency`  | ~1 Hz broadcasts (386 per sim / 4 min) | < 5s | ✅ PASS |

## Emergent Behavior

**Daisy was the social one.** Of her 22 `walk-to` calls, she targeted 3 distinct objects (44, 48, 28) — object 28 is Gerry. She tried to approach him and spoke twice:

1. *"Morning, Gerry! What a mess in the bathroom — I'm going to start cleaning up these flood tiles…"*
2. *"Morning, Gerry! How are you doing today?"*

Compare to the prior session where Daisy was stuck in a 64-intent rejection loop on the exercise bench. This time she explored, tried to converse, varied her targets. That's the big behavioral delta — and it happened *despite* the agent still running on Opus (same model family).

**Gerry stayed nearly stationary** but was more active than last session — 19 `interact-with` attempts across 8 distinct object/interaction combos (up from 0 varied actions). Still never used `walk-to` or `speak`, so his fulfill rate took a hit from timing-out IPC interactions.

## Findings

### Bugs

- **IPC interact/walk-to times out at 30s under load.** Gerry's 3 errors and Daisy's 1 error were all `Command timed out after 30.0 seconds` on correlated IPC responses. Suggests the VMIPCDriver response channel isn't always returning a fulfillment for `interact-with` when the action is long-running or rejected. Needs a shorter timeout with explicit "still running" vs "rejected" semantics, or a way for the agent to ask "did that finish?".

- **`Neighborhood.cs:97` loaded `Effects\colorpoly2d` (lowercase d) against the `colorpoly2D.xnb` asset.** Case-sensitive Linux FS meant this always failed; previously masked by a 15-byte corrupt `colorpoly2d.xnb` duplicate. Fixed in this session (source change, pending commit).

### Gaps

- **Agent still on Opus 4.7** (`ClaudeAgentOptions` has no `model=` in `sim-agent-v4.py:202-207`). Per project token-optimization rules this should default to Haiku. One-line fix; untaken this run so we had parity with the last playtest's model.

- **Gerry never used `walk-to` or `speak`.** The agent choice is deterministic given his perception — if all of it points at nearby objects, he'll keep trying `interact-with` forever. System prompt doesn't encourage navigation or conversation as first-class options. Either the prompt needs a "consider moving or speaking when stuck" nudge, or the perception stream should surface conversation openings more prominently.

### Critical (not observed this run)

- **Xvfb instability on this host.** Reel capture failed: Xvfb segfaulted partway through the run (NVIDIA EGL abort, stack trace in `game.log` / dmesg). The game stayed up — it never needed the X display again after boot, apparently — but `scrot` couldn't attach. Need either a software-only Xvfb invocation (`LIBGL_ALWAYS_SOFTWARE=1`, drop nvidia EGL from the ld path) or an alternative headless display (Xdummy, Xephyr, or a nested Wayland compositor).

### Delights

- **Daisy's spontaneous small-talk** is exactly the kind of emergent behavior we want. It happened without any prompt-level coaching; it emerged from the convention surface + perception alone.
- **2x behavioral diversity** over the last playtest, on the same model, the same agent script, the same lot. The fixes that landed between sessions (perception idle-gate drop, sidecar stdout race, SimAntics modal suppression) materially changed what the agent could perceive and respond to.

## Artifacts

All under `docs/playtest/20260417T145215Z/`:

- `report.md` — this file
- `agent-daisy.jsonl` — 85 events (intent/fulfillment/error/perception trace)
- `agent-gerry.jsonl` — 93 events
- `agent-daisy-stderr.log`, `agent-gerry-stderr.log` — Python stderr
- `game.log` — SimsVille stdout+stderr (13 KB)
- `sidecar.log` — Go sidecar, every broadcast tagged (974 KB)
- `reel/` — empty (Xvfb crashed before capture)

## Next

1. Patch `scripts/sim-agent-v4.py` to pin `model="claude-haiku-4-5"` — cheaper, faster turn-taking; rerun and compare.
2. File rd: "IPC correlated-response 30s timeouts on `interact-with`/`walk-to`" — agent blocks on convention calls that don't emit fulfillment frames.
3. File rd: "Xvfb segfaults via NVIDIA EGL on workshop" — pin software GL, or use Xdummy.
4. Patch `CLAUDE.md`: (a) "C# binary not rebuildable locally" is false, (b) "committed .xnb files are MGFX v10" is now true after `1eaedde`, (c) workshop-via-SSH recipe is obsolete on this host.
