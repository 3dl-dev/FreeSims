---
date: 2026-04-17
status: review complete; iteration plan proposed; not started (project pivoted to FreeSO)
based_on: playtests 20260417T04XX (Opus, pre-fix) and 20260417T145215Z (Opus, post-fix)
reference_implementation: galtrader (~/projects/galtrader)
---

# Embodied Agent Design Review — FreeSims

A review of why LLM-driven Sims on the FreeSims campfire-convention architecture produce weak, pattern-locked behavior, and a proposed iteration plan. Written after two playtests that both used Opus 4.7 as the per-Sim brain. The question that triggered the review: *"Sonmet worked great on galtrader. Why does it suck here?"* The answer turned out not to be about the model.

## TL;DR

**It's not the model. It's the information diet.** The agent is asked to embody a character in a rich simulation with:

- **Sterile convention descriptions** that deliberately omit cause/effect/prerequisites ("Just be. Not every moment needs action."),
- **A passive perception** of the world (motive snapshots, no deltas, no "what I could do", no "why my last action failed"),
- **A severed feedback loop** — when the game rejects an action via dialog ("not in a good enough mood to work out"), the agent never hears about it,
- **A blocking intent loop** that times out at 30s per action and drops perception ticks under load.

Any LLM will produce mediocre gameplay on this substrate. Galtrader's agent works well on Sonnet because its API carries prerequisites, effects, and costs in the convention descriptions themselves (*"Purchase a new ship class, trading in your current vessel for 50% value"* vs. FreeSims's *"Buy something for your home"*).

## Evidence — two playtests, same substrate

### Playtest 1 — early AM, pre-fix (Opus)
Gerry: 95 intents, all `wait`. Daisy: 64 intents, 64 of them identical `interact-with object=42 interaction_id=0` (Work Out on exercise bench, refused each time).

### Playtest 2 — after perception idle-gate drop, sidecar stdout race fix, SimAntics modal suppression (still Opus)
- Daisy: 28 intents, 4 distinct ops (`walk-to`, `speak`, `interact-with`, `wait`), 92% non-wait fulfillment, initiated two spontaneous speak attempts including social.
- Gerry: 35 intents, 2 ops (`interact-with` 19 different arg combos, `wait` 16), 81% non-wait fulfillment.
- One Sim roughly doubled behavioral diversity; the other pattern-locked on interact-with and never used `walk-to` or `speak`.

Both results are on Opus, same agent script, same prompt structure. The engine-level fixes between runs (dialogs not blocking the render thread, perception emitting reliably for both sims) materially changed what reached the agent, which changed what the agent did.

## Architecture as the agent sees it

The agent is `scripts/sim-agent-v4.py`. Each turn:

1. **Perception arrives** as a tagged campfire broadcast (`freesims:perception` + `sim:<persist_id>`). Payload is a JSON dict with `motives`, `position`, `nearby_objects[].interactions[]`, `lot_avatars`, `skills`, `relationships`, `action_queue`, `current_animation`.
2. **Conventions are the action API.** Loaded once at startup from `convention:operation` messages on the lot. 16 declarations (13 served by the sidecar, 3 unserved stubs).
3. **System prompt** is rendered from the conventions + a first-person framing: *"You are {name}. This is your life."*
4. **LLM picks one intent** as `{"op": "...", "args": {...}}`.
5. **Agent invokes** `cf <lot> <op> --args` as a blocking subprocess. 30s timeout per call.
6. **Loop.**

What the agent **does not** see:
- Dialog frames from the game (rejection messages, notifications, confirmations). `VMIPCDriver.HandleVMDialog` emits them on a separate IPC channel → sidecar puts them on `ipc.Client.DialogCh` → **only ever printed to stdout** (`sidecar/main.go:208`). Since our `--campfire` mode disables stdout bridging to avoid racing the campfire bridge, dialogs go into a black hole.
- Outcomes of its previous intent. The fulfillment subprocess return is logged to disk but not fed into the next perception.
- Motive deltas or rate-of-change.
- What each interaction *does* (the `interactions[]` list on each object has `{id, name}` only — no effect description, no gating condition).
- What `current_animation: "a2o-idle-neutral-lstand-fidget-1c"` means in human terms.

## Findings by severity

### 🔴 Critical — the feedback loop is severed
`VMIPCDriver.cs:566 HandleVMDialog` correctly emits dialog frames (title, text, buttons) for every game-level dialog — including rejection messages like *"I'm not in a good enough mood to work out"*. Sidecar `main.go:206-210` writes those to stdout only. In `--campfire` mode, that stdout path is gated off by the fix in commit 46dd082 (which was correct — it prevented a racing second consumer on the perception channel). **Net effect: the agent cannot learn from being told no.** This single issue explains Daisy's 64-intent rejection loop in the first playtest.

### 🟠 High — convention descriptions omit mechanics by design
From `sidecar/conventions/*.json`:

| Op | Current description |
|---|---|
| `walk-to` | "Go somewhere. Head toward a place or a person." |
| `speak` | "Say something. Anyone nearby will hear you." |
| `wait` | "Just be. Not every moment needs action. Sometimes you sit with your thoughts." |
| `interact-with` | "Do something with a nearby object. Your perception shows what is near you and what each thing offers — a fridge might let you have a snack, a bed lets you sleep, a mirror lets you practice your speech. Pick the object and the interaction." |
| `remember` | "Hold onto a thought. Your memories carry forward..." |

Contrast galtrader (`~/projects/galtrader/pkg/server/conventions/`):

| Op | Galtrader description |
|---|---|
| `land` | "Engage landing computer toward a planet surface (must not already be landed). **Requires Docking Computer equipment.**" |
| `buy-ship` | "Purchase a new ship class, **trading in your current vessel for 50% value**" |
| `complete-mission` | "Complete a delivery mission at the destination" |

Every galtrader description carries **prerequisite, cost, or outcome**. FreeSims descriptions read as lived-experience poetry. The CLAUDE.md design principle is explicit: *"No behavioral coaching in prompts."* That principle is the root cause — an ideological commitment that starves the agent of the scaffolding a character needs to act with purpose.

### 🟠 High — perception is passive
`PerceptionEmitter.BuildPerception` produces a snapshot of world state. There are no:
- **Motive deltas.** `hunger: 71` — agent doesn't know if it's rising or falling or stable. Has to compare across turns without a formal diff mechanism.
- **Available-actions hints.** The agent has to compute "am I close enough to interact with object 42?" from raw coordinates. That's fine for a classical planner; it's friction for an LLM acting on vibes.
- **Interaction effects.** Each interaction is `{id, name}`. "Work Out" doesn't carry `effects: ["+fun","+body skill"]` or `gate: ["mood >= 40"]`.
- **Recent-events list.** What just happened to me? What did I just try? What did the game say? All erased each tick.
- **Human-readable animation.** `a2o-idle-neutral-lstand-fidget-1c` is engine-internal.

### 🟡 Medium — system prompt is spiritual, not mechanical
The `render_universe_prompt` function in `sim-agent-v4.py:106-156` frames the agent as living-its-life:
> *"Your motives — hunger, comfort, energy, hygiene, bladder, social, fun, mood — tell you how you feel. When one drops, you feel it. What you do about it is your choice."*

It doesn't say *motives degrade by default — you must act to restore them.* It doesn't say *walking-to is a prerequisite for interact-with beyond a few tiles.* It doesn't say *wait is almost never the right answer.* The agent has to re-derive the Maslow hierarchy from scratch on every call.

### 🟡 Medium — intent loop is blocking and lossy
`sim-agent-v4.py:272` calls `invoke_convention` which shells out to `cf <op> --args` and waits. The 30s cf-subprocess timeout means a stuck interact-with eats a half-minute of wall clock. Meanwhile perception frames arrive and get skipped by the `cursor > ts` advance scheme. The agent answers stale data.

### 🟡 Medium — Gerry-style pattern lock
Even with a working feedback loop, an agent that falls into "try interact-with repeatedly" never discovers `walk-to` or `speak` because those options aren't reinforced by the prompt or perception. An LLM acts on what's salient. The current prompt makes "do something" salient (the list of 13 verbs) but makes no verb salient over any other.

## Iteration plan

Four workstreams, ordered by expected impact per unit of effort.

### Workstream A — Close the feedback loop (1 file, biggest uplift)
- **A1.** `sidecar/campfire.go`: bridge `ipc.Client.DialogCh` onto campfire broadcasts tagged `freesims:perception` + `sim:<caller_persist_id>`, so the agent's existing poller picks them up. Payload shape: `{"type":"dialog","title":"...","text":"...","buttons":["OK"]}`.
- **A2.** Extend `PerceptionEmitter.BuildPerception` to include a `recent_events[]` array carrying the last N seconds of dialogs, pathfind failures, and IPC-fulfillment results for this Sim. Cap at 10 entries; oldest first.
- **A3.** Agent patch: merge `recent_events` into the user message so the LLM sees what just happened.

### Workstream B — Teach conventions what they do (16 JSON edits)
- **B1.** Rewrite every description to carry at least one of: **prerequisite**, **effect**, **cost**. Examples:
  - `walk-to`: *"Travel to a position or within ~2 tiles of a target. Required before `interact-with` on a distant object. Takes seconds depending on distance; interrupts if blocked."*
  - `speak`: *"Say something aloud. Sims within ~5 tiles hear you. Raises social motive slightly; the listener may (or may not) respond on their own turn."*
  - `interact-with`: *"Use a nearby object — check the object's `interactions` list for what's available. Each interaction has an `effects` hint describing which motives it restores and any gating condition."*
  - `wait`: *"Skip this tick. Motives fall while you wait — use only when you truly have nothing to do and are not trying to recover from a stuck state."*
- **B2.** Add `examples` field to high-use conventions so the LLM has shot-primer material in the system prompt.

### Workstream C — Make perception self-documenting (C# changes, rebuild)
- **C1.** Motive schema: `hunger: {value: 71, rate_per_minute: -2, trend: "falling"}`. Requires a rolling window in `PerceptionEmitter`.
- **C2.** Interaction effects: attach an `effects` hint to each interaction, loaded from a `Content/interaction_hints.json` lookup table keyed by `{object_catalog_name: {interaction_name: {effects: [], gates: []}}}`. Start with 20 hints covering common objects (bed, fridge, toilet, shower, exercise bench, mirror, TV, radio, phone, sink, couch, chair, tub, computer, bookshelf, door, window, mailbox, trash can, pool). Populate from Sims 1 design knowledge or from observed IFF Cat/Name chunks.
- **C3.** Animation name translation: `a2o-idle-neutral-lstand-fidget-1c` → `"standing idle, fidgeting"`. Regex-based mapping in a sidecar post-processor is fine to start.
- **C4.** Populate `recent_events[]` (from A) with a stable schema:
  ```
  {"t": <ts>, "kind": "dialog"|"fulfillment"|"pathfind_fail", "text": "...", "op": "...", "ok": bool}
  ```

### Workstream D — Unblock the agent
- **D1.** Make `interact-with` and `walk-to` fire-and-forget by default (non-correlated). Outcome flows through the next perception's `recent_events`. Keep correlated mode for `query-*`, `save-sim`, `load-lot`.
- **D2.** Drop cf-subprocess timeout from 30s to 2s for non-correlated ops.
- **D3.** In the agent poller, keep a small ring buffer per sim so slow LLM turns don't miss ticks.

### Order
1. **A1+A2+A3** (one evening of work, biggest single uplift).
2. **B1** (one evening of JSON edits; no rebuild needed).
3. **D1+D2** (one agent-script patch + one sidecar flag).
4. **C1** (motive deltas — small C# change, rebuild).
5. **C2** (interaction hints table — content-heavy; start with 20 objects).
6. **C4** (recent_events schema stabilization).
7. **C3** (animation translation — polish).
8. **Playtest on Sonnet** for apples-to-apples with galtrader's success baseline.
9. Only **then** evaluate Opus vs Sonnet head-to-head.

## Design principle to revisit

The CLAUDE.md line *"the agent IS the Sim, not a puppet controller; convention descriptions are first-person lived-experience; no behavioral coaching in prompts"* is a clean idea but it's **anti-informational** against the current hardware (stateless LLM call per tick, two-turn budget, no built-in knowledge of this specific lot or character history). The character-continuity illusion has to come from somewhere; right now neither perception nor conventions nor the system prompt supply it, so the LLM invents an abstract "be a Sim" simulation on the fly — badly.

Two ways forward, both legitimate:

- **Keep the principle; compensate via persistence.** Let the agent `remember` aggressively, feed memories into every perception, build up lived experience across ticks. This is harder but more faithful to the stated design.
- **Relax the principle; let conventions teach mechanics.** Galtrader's approach. Less philosophically pure but demonstrably works on Sonnet.

Recommended: **relax, for now, to unblock playable behavior, then layer the first-person/memory approach on top once the base works.** An agent that can act purposefully can *also* remember; an agent stuck in a rejection loop will never build interesting memory.

## Where this applies if we pivot to FreeSO

The FreeSO upstream likely kept the server/client multiplayer architecture. Porting this work means either:

1. **Keep it local** — use `VMLocalDriver` on FreeSO too, add the same IPC bridge, run 1 client = 1 lot = N agents. This is what FreeSims does.
2. **Go multiplayer** — the sidecar talks to the FreeSO server, not to a client's VM. Agents connect as avatars. Much more work; the perception emitter would be a server-side component.

Regardless of path, the findings here are **architecture-independent**:
- Convention descriptions must carry mechanics.
- Perception must surface motive deltas, available actions, recent events.
- Dialog frames must reach the agent.
- The intent loop should be non-blocking.

## Files to reference
- Agent: `scripts/sim-agent-v4.py`
- Conventions: `sidecar/conventions/*.json` (16 files)
- Sidecar convention bridge: `sidecar/campfire.go`
- Sidecar IPC client / channels: `sidecar/protocol/client.go`, `sidecar/main.go` (lines 88-210)
- Perception emitter: `SimsVille/SimsAntics/Diagnostics/PerceptionEmitter.cs`
- External controller gate: `SimsVille/SimsAntics/Diagnostics/ExternalControllerRegistry.cs`
- VM IPC driver (dialog emitter): `SimsVille/SimsAntics/NetPlay/Drivers/VMIPCDriver.cs:566 HandleVMDialog`
- Prior playtests: `docs/playtest/20260417T145215Z/`

## Status
Reviewed, not implemented. Project pivoting to FreeSO. This document + `docs/freeso-handoff.md` are the knowledge transfer.
