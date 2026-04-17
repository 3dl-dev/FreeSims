# Embodied Sim Agents — Design

Input spec for `/swarm-plan`. Captures architecture, knowledge boundaries, tool surface, attention model, memory strategy, verification, and ordering. Sufficient for a fresh agent to read cold and execute.

## Context

**Current state (master, post reeims-2de).** Two Python heuristic agents (`scripts/sim-agent-v2.py`) drive two Sims via god-mode IPC:
- Read: full perception JSON on stdout of sidecar. Includes own motives AND every other Sim's exact motives, exact funds, exact skills/job/relationships.
- Write: structured commands (`buy`, `interact`, `chat`, `query-catalog`, `goto`) on stdin. Action selection is `if hunger < 30: buy_cheapest_snack` rules.
- Identity: third person ("Gerry: funds=0"). The agent has a Sim, the Sim is a target.

**Problem.** Architecture bakes in a puppeteer frame. Even if the rules were replaced by an LLM, the data surface (god-mode perception, god-mode action verbs, omniscient nearby Sim readouts) would keep the LLM in operator-mode rather than occupant-mode.

**Goal.** Make each Sim feel inhabited — the LLM reasons as the Sim, acts through actions the Sim has, and knows what the Sim would know. No narrator service, no prose translation layer. The ego shift happens at two boundaries: (1) perception hygiene in C#, (2) tool surface in Python. Everything else is stock Anthropic tool-use.

**Non-goals.**
- Replacing the sidecar, changing IPC wire format, changing framing protocol.
- Adding a second LLM layer (narrator / translator / mediator).
- Cross-session persistent memory (save/load of agent mind state).
- LLM-driven world-building or admin operations.

## Architecture

```
SimsVille (C#)  ──IPC socket──  sidecar (Go)  ──stdio──  sim-agent-v3.py (per Sim)
       │                                                          │
       │                                                          ├── attention controller
       ↓                                                          ├── Anthropic tool-use loop (Haiku)
   PerceptionEmitter.BuildPerception(godMode)                     ├── tool handlers
                                                                  └── memory / compaction
```

**What changes.**

| Layer | Change | Files |
|---|---|---|
| C# | Perception hygiene: `godMode` boundary | `SimsVille/SimsAntics/Diagnostics/PerceptionEmitter.cs` |
| Go | None | n/a |
| Python | New agent using Anthropic SDK | new `scripts/sim-agent-v3.py` |
| Demo | Launcher swaps v2 → v3 | `scripts/demo-multi-agent.sh` |

**What stays.** Sidecar IPC protocol unchanged. Command wire format unchanged. `demo-multi-agent.sh` harness pattern unchanged. Existing 15 PRs (Tier 1/2/3 command surface) untouched.

## Knowledge boundary — the C# change

Current `BuildPerception` emits this for each entry of `lot_avatars[]`:

```json
{"persist_id":N,"name":"Daisy","position":{...},"current_animation":"...",
 "motives":{"hunger":75,"comfort":75,"energy":75,"hygiene":75,
            "bladder":75,"social":75,"fun":75,"mood":75}}
```

That motives object is god-mode — Gerry doesn't have telepathy. Embodied perception:

```json
{"persist_id":N,"name":"Daisy","position":{...},"current_animation":"...",
 "looks_like":"scanning the kitchen, shoulders slumped"}
```

**Rule.** A Sim knows its own body in full (motives, funds, skills, job, relationships, inventory). A Sim observes others through `looks_like` + `current_animation` + `position`. No numeric readout of another Sim's internal state.

**`looks_like` synthesis.** Derived in C# at emission time from the observed Sim's real motives + animation. Short string (≤ 60 chars). Composition rule:
- Primary driver — lowest motive below threshold:
  - `hunger < 20` → "hungry"
  - `energy < 20` → "tired"
  - `bladder < 20` → "fidgeting"
  - `social < 20` → "looking for company"
  - `hygiene < 20` → "unkempt"
- Secondary — mood: `mood < 30` → "glum", `mood > 80` → "cheerful"
- Animation hint: `current_animation` mapped to a verb ("eat" → "eating", "walk-to" → "walking somewhere", "idle-sit" → "sitting", "idle-stand" → "standing", "chat" → "chatting")
- Composed: `"<animation hint>, <primary>, <secondary>"` with empty slots omitted
- Fallback: `"idle"` if nothing salient

**Feature flag.** `FREESIMS_GOD_MODE=1` env var preserves the current full-motive emission for tests and debugging. Default (unset) applies the hygiene boundary.

**Self-exclusion.** Parent item `reeims-221` is a hard prerequisite. Today Gerry's perception includes Gerry in `lot_avatars`. Must be fixed before embodiment work begins — embodied Gerry seeing himself in the room breaks identity.

**What stays in perception.** Self: motives, funds, clock, position, current_animation, action_queue, skills, job, relationships, nearby_objects. World: clock, nearby_objects (objects Gerry can see). Events: dialog, pathfind-failed. Chat received (direct address — see Social below).

**What does NOT get filtered.** `nearby_objects` stays god-mode-ish (Gerry "sees" objects via game rendering anyway — his knowledge of object positions is legit). But `query-catalog` is not in the embodied tool surface; the agent can only see objects already present in `nearby_objects`.

## Tool surface — Sim affordances, not IPC

Claude Haiku gets these six tools. The handler translates intent to IPC.

```python
TOOLS = [
    {
        "name": "walk_to",
        "description": "Walk to a named place or another Sim. Valid targets: 'front_door', 'kitchen', 'bathroom', 'bedroom', 'living_room', or the name of any Sim currently in your lot_avatars. Returns when you arrive or the path is blocked.",
        "input_schema": {
            "type": "object",
            "properties": {"target": {"type": "string"}},
            "required": ["target"],
        },
    },
    {
        "name": "interact",
        "description": "Use an object or engage a Sim near you. target must be a name visible in nearby_objects or lot_avatars. Optional verb disambiguates when an object offers multiple interactions (e.g. 'eat', 'sleep', 'shower'). If omitted, the primary affordance is chosen.",
        "input_schema": {
            "type": "object",
            "properties": {
                "target": {"type": "string"},
                "verb": {"type": "string"},
            },
            "required": ["target"],
        },
    },
    {
        "name": "say",
        "description": "Speak aloud. Anyone in earshot hears you. Keep utterances short and conversational.",
        "input_schema": {
            "type": "object",
            "properties": {"text": {"type": "string"}},
            "required": ["text"],
        },
    },
    {
        "name": "look_around",
        "description": "Pause and observe your surroundings in detail. Returns an enriched perception snapshot.",
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "remember",
        "description": "Pin a thought or observation to memory. Use for things you want to recall later (e.g. 'Daisy seemed upset after dinner'). Use sparingly — 1-2 per conversation.",
        "input_schema": {
            "type": "object",
            "properties": {"note": {"type": "string"}},
            "required": ["note"],
        },
    },
    {
        "name": "wait",
        "description": "Let time pass without doing anything. Use when idle or waiting for someone to respond. Duration is in-game seconds.",
        "input_schema": {
            "type": "object",
            "properties": {"seconds": {"type": "integer"}},
            "required": ["seconds"],
        },
    },
]
```

### Tool → IPC mapping

| Tool | Handler logic | IPC emitted |
|---|---|---|
| `walk_to(target)` | Resolve target: (a) if matches a Sim name in current `lot_avatars`, use that Sim's position; (b) else look up in landmark table (see below); (c) else error back to LLM | `{type:"goto",actor_uid:self,x,y,level}`; await position-match in own perception or `pathfind-failed` event |
| `interact(target, verb)` | Scan `nearby_objects` + `lot_avatars` for target name. Resolve `(object_guid, action_id)` pair. Action IDs from a verb→action_id mapping (needs a new `query-pie-menu` command — see Open questions). If verb omitted, pick primary | `{type:"interact",actor_uid:self,target_object_id,action_id}`; return outcome from response frame |
| `say(text)` | Truncate to 140 chars. No escape concerns — sidecar JSON-encodes | `{type:"chat",actor_uid:self,message}` |
| `look_around()` | Trigger a `query-sim-state` for self, return enriched payload to LLM as tool result | `{type:"query-sim-state",actor_uid:self,request_id}` await response |
| `remember(note)` | Local — append to `memories: List[str]`. Surface in subsequent system prompts. No IPC | — |
| `wait(seconds)` | Block handler (not the LLM) until clock advances `seconds` in-game time. LLM loop idle in meantime | — |

### Landmark table (MVP)

Built at agent startup by scanning `nearby_objects` in the first perception for category hints:

```python
LANDMARKS = {
  "kitchen":     nearest object with name containing "refrigerator" or "stove",
  "bathroom":    nearest object with name containing "toilet" or "shower",
  "bedroom":     nearest object with name containing "bed" and not "pet",
  "living_room": nearest object with name containing "sofa" or "couch" or "television",
  "front_door":  object with name containing "door" AND position on lot edge,
}
```

If a landmark can't be resolved on first pass, it's absent from the tool's description for that Sim ("Valid targets: kitchen, bathroom, a Sim's name" rather than listing all).

### Excluded (god-mode) — do NOT expose

`query-catalog`, `buy`, `place-inventory`, `send-to-inventory`, `update-inventory`, `load-lot`, `save-sim`, `load-sim`, `query-wall-at`, `query-floor-at`, `query-tile-pathable`. None of these map to a body-scoped verb. They stay in the IPC for admin/scripted scenarios but aren't registered with Claude.

## Attention — don't think every tick

Perception arrives ~1 per in-game second. Calling Haiku on every perception is ~600 LLM turns per Sim per 10-minute demo. A conscious human re-decides far less often. Model:

### Intent state

Agent carries a current intent until complete, failed, or timed out:

```python
@dataclass
class Intent:
    description: str              # free-text, LLM-written ("cooking breakfast")
    origin_tool: str              # which tool call started it ("interact")
    started_tick: int             # in-game tick when started
    expected_complete_tick: int   # soft deadline
    completion_signal: str        # "arrived" | "action_done" | "dialog_resolved" | "timer"
```

### Wake triggers — when to invoke the LLM

`should_think()` returns True if any of:

1. **No current intent** — Sim is idle, time to decide.
2. **Intent complete** — observed signal fires (arrival, action_done from response frame).
3. **Intent failed** — `pathfind-failed` event or command error response.
4. **Intent timed out** — `current_tick > expected_complete_tick + grace`.
5. **Motive crosses threshold** — any motive drops below 30 from a prior above-30 reading.
6. **Direct address** — another Sim's `chat` message names this Sim (e.g. "Gerry, ...") or is the only other Sim in the room.
7. **Dialog event** — `dialog` frame arrives.
8. **Periodic** — last LLM call was ≥ 60 in-game seconds ago (keeps unresponsive idle from deadlocking).

Between triggers, the attention controller accumulates perceptions into a buffer (most recent + a summary) but does not call Claude.

### Wake input

When `should_think()` returns True, the LLM sees:

- System prompt (static, cacheable): identity, worldview, tool descriptions
- Last compaction summary (if any)
- Recent conversation turns (most recent ~20)
- **New** user message with structured body:
  ```
  <trigger>{trigger_reason}</trigger>
  <perception>{latest perception JSON}</perception>
  <recent_events>{chat messages addressed to you, dialogs, pathfind failures since last wake}</recent_events>
  <memories>{pinned memories}</memories>
  ```

The LLM either calls a tool or returns a text response (which the agent logs and then treats as end-of-turn).

## Memory

### Live state

```python
@dataclass
class AgentState:
    name: str
    persist_id: int
    conversation: List[MessageParam]   # Anthropic format, tool_use/tool_result turns
    memories: List[str]                # pinned by remember() tool
    current_intent: Optional[Intent]
    last_think_tick: int
    landmarks: Dict[str, int]          # name → object GUID
```

### Compaction

Conversation grows unbounded; compaction runs when `len(conversation) > 40`:

1. Take oldest 25 turns, send to Haiku with system prompt: "Summarize these turns of <name>'s day in 3-5 sentences, first person, preserving salient facts."
2. Replace those 25 turns with one system message: `Earlier today: {summary}`.
3. Keep conversation ≤ 20 turns post-compaction, always.

Compaction is a separate Haiku call. Cost: ~1 every 40 turns, ~3k input + 200 output.

### Prompt caching

Anthropic's automatic caching keyed on stable prefix. The cacheable portion:
- System prompt (identity, worldview — static per Sim per session)
- Tool definitions (static across session)
- Compaction summaries (update on compaction event only)

Dynamic portion (~1-2k tokens new per turn): recent turns + new perception/event frame.

## Cost model — Haiku

Per turn (post-cache warm-up):
- Cached input (~2k tokens, system + tools): ~$0.0002 (at Haiku cache-hit rates)
- New input (~1.5k tokens, perception + recent turns): ~$0.0012
- Output (~200 tokens tool call): ~$0.0008
- **~$0.002 per turn**

Per demo (4 Sims × 15 in-game min × ~20 turns/Sim via attention controller):
- ~80 turns total
- ~$0.16 + one-time cache writes (~$0.01)
- Compaction: ~2 passes per Sim = ~8 × $0.005 = $0.04
- **Total ~$0.20 per full demo run**

Per-Sim turn budget: if observed to exceed 30 turns per 10-in-game-min, the attention controller is too permissive; tune thresholds.

## Social — conversation falls out

Two Sims can hold a conversation without orchestration:

1. Sim A's `say("Pass the salt, Gerry?")` → `{type:"chat",actor_uid:A,message:...}` → VM emits as a chat balloon → all nearby Sims perceive it in their next perception under `nearby_chat` or in a direct `chat_received` event (needs small C# addition — see Open).
2. Sim B's attention controller treats direct address (message contains B's name, or B is only other Sim in room) as wake trigger #6.
3. Sim B's next LLM turn sees `<recent_events>Alice said to you: "Pass the salt, Gerry?"</recent_events>`.
4. Sim B calls `say("...")` in response.

Turn-taking is implicit via wake semantics. No turn-manager service. Back-pressure if both Sims talk simultaneously: fine — real conversations have overlap.

**Small C# addition.** Chat messages currently emit as in-game balloons but may not reach IPC perception for other Sims. Need to verify — if not, add a "chat_received" event frame. See Open questions.

## Verification

VNC is back at `localhost:5900` — manual observation of visible behavior is viable. But automated assertions should not require the human.

### Logging contract

Each agent writes `/tmp/embodied-agent-<persist_id>.jsonl` with structured records:

```json
{"ts":"...","event":"turn","trigger":"dialog","intent":"answer_dialog","tool":"interact","args":{"target":"dialog","verb":"yes"}}
{"ts":"...","event":"perception","clock":{...},"motives":{...},"lot_avatars":[...]}
{"ts":"...","event":"ipc_out","cmd":{"type":"goto","x":12,"y":8}}
{"ts":"...","event":"ipc_in","frame":{"type":"response","status":"ok"}}
{"ts":"...","event":"compact","old_turns":25,"summary":"..."}
```

### Automated assertions (run at demo end)

1. Each agent made ≥ 5 LLM turns.
2. Each agent used ≥ 2 distinct tools.
3. At least one exchange: agent A `say` → agent B `say` within 5 turns.
4. **Hygiene check:** scan agent's conversation for evidence it saw another Sim's numeric motive. If any `"hunger":<N>` or similar appears in a `<perception>` block outside self, godMode leaked.
5. **Identity check:** scan agent's reasoning (LLM text output or tool arg rationales) for third-person references to self ("Gerry will..."). First-person expected; third-person = prompt failed.
6. No `pathfind-failed` unresolved — every failure must be followed by a new intent within 3 turns.
7. No tool-call errors in last third of demo (steady state reached).

Assertions run by `scripts/verify-embodied-demo.py` after demo script exits. Failures produce structured findings; human can read + triage.

### Subjective evaluation

Human watches via VNC for a 5-minute sample, scores: does this look like people living, or bots obeying rules? Qualitative notes logged to `docs/embodied-demo-report.md` at end of each run.

### Screenshots

`scrot -d 30` via demo script — every 30s a timestamped PNG in `/tmp/embodied-ss/`. No X viewer needed.

## Prerequisites (must land before this tree)

Hard blockers:
- **reeims-221** — Gerry self-exclusion. Can't embody a Sim that sees itself in the room.
- **reeims-2c1** — IPC send race. Sporadic disconnects mid-demo corrupt agent state.
- **reeims-c8c** — `payloadLen=0` misalignment. Parser can desync silently.

Recommended (not hard blocks):
- **reeims-5e4** — buy-not-debiting. Embodied agents don't use buy, so bug is orthogonal; fix when convenient.
- **reeims-d77** — PerceptionEmitter live data paths. Test coverage gap; doesn't block functionality.

## Open questions (resolve during swarm-plan or first implementation)

1. **Chat reception.** Does `{type:"chat"}` from one Sim appear in other Sims' perception today? If no, need a `chat_received` event frame. Check by reading PerceptionEmitter + VMChatBalloon.
2. **Pie-menu resolution.** How does `verb → action_id` work? Need a `query-pie-menu(object_guid)` command, or hardcode a verb map for common affordances (eat, sleep, shower, watch). Start with hardcoded; add query if limiting.
3. **`action_id` for interact.** Current `interact` command may not accept an action_id param. Verify and extend if needed.
4. **Wait semantics.** Does the LLM call `wait()` and block, or is `wait()` a no-op that defers the next `should_think()` wake? Recommend: no-op that updates `next_wake_tick`.
5. **API key provisioning.** `ANTHROPIC_API_KEY` env var in the demo launcher. Add to `.env` handling in `scripts/demo-multi-agent.sh`.

## Proposed decomposition (for `/swarm-plan`)

Each child is an outcome, one-session sized, self-contained post-compaction. Treat this list as a seed — swarm-plan will refine, wire deps, sanity-check sizing.

1. **C# PerceptionEmitter godMode boundary.** `looks_like` field synthesis, other-sim motive zeroing when !godMode, `FREESIMS_GOD_MODE=1` preserves current behavior. Done: in a demo run with godMode off, `lot_avatars[].looks_like` is a non-empty string and all `lot_avatars[].motives.*` are zero; with godMode=1, motives carry real values.

2. **Python agent v3 skeleton: Anthropic tool-use loop.** New `scripts/sim-agent-v3.py`. Imports anthropic SDK. Main loop: read perception from stdin, append to conversation, call Haiku with the 6 tools, dispatch tool call via handler, log to `/tmp/embodied-agent-<pid>.jsonl`. System prompt is first-person. All 6 tool handlers present (stubbed `interact` and `walk_to` OK; `say`, `remember`, `wait`, `look_around` fully wired). Done: script runs against existing demo harness, ≥ 10 LLM turns, each produces a valid IPC command, log shows first-person reasoning.

3. **Attention controller with intent state.** `Attention` class with 8 wake triggers. `Intent` dataclass with completion detection. Integrate into v3 main loop — perceptions accumulate between wakes; Claude only called when `should_think()` True. Done: 10-in-game-min demo has ≤ 30 LLM turns per Sim and log shows `trigger=<reason>` for each.

4. **Tool handler: walk_to with landmark resolution and pathfind-failed handling.** Landmark table built from first perception. Sim-name targets route to that Sim's current position. Returns structured tool result the LLM sees: `"arrived at kitchen"` or `"can't reach kitchen — something is blocking"`. No infinite retry. Done: agent asked to walk to a reachable target arrives; asked to walk to a blocked target, the LLM sees the failure and replans.

5. **Tool handler: interact with verb resolution.** Implement (object_name, verb) → (guid, action_id) lookup. Start with a hardcoded verb map (eat, sleep, shower, watch, sit, read) — each maps to a known action_id for the matching object category. If `query-pie-menu` is needed, file as followup. Done: `interact("refrigerator", "eat")` triggers an `eat` pie-menu action in the VM and hunger motive rises in the next perception.

6. **Chat reception wiring (if missing).** Verify whether Sim A's `chat` appears in Sim B's perception. If not, emit `chat_received` event frame from VMIPCDriver on every chat execution, routed to a new `ChatCh` on the sidecar, surfaced in agent stdin as JSONL. Done: A says "Hello"; B's agent log shows an event with text="Hello" and sender=A.

7. **Memory compaction pass.** Conversation > 40 turns triggers summarization. Extracts oldest 25 turns, summarizes via Haiku, replaces with one system message. Done: simulated 50-turn input produces < 30 messages post-compaction, first message is a summary, cache-hit rate for stable prefix remains high.

8. **Verification harness.** `scripts/verify-embodied-demo.py` reads agent JSONL logs and runs the 7 assertions. Structured failures, human-readable output. Done: run against a known-good demo log → all green; run against a log with god-mode leakage → red with specific finding.

9. **Demo: 2 embodied Sims × 15 in-game min.** Launch `sim-agent-v3.py` × 2 via updated `demo-multi-agent.sh`. Capture logs, perception JSONL, screenshots via scrot every 30s. Run `verify-embodied-demo.py`. Write `docs/embodied-demo-report.md` with subjective notes + assertion results. Done: all assertions green; report written; human reviews 5-min VNC sample and notes whether behavior subjectively reads as "living."

### Ordering / dependencies

- 1 blocks 9 (demo needs the hygiene boundary)
- 2 blocks 3, 4, 5, 7 (all work inside the v3 loop)
- 3 blocks 9 (runaway LLM calls would crash the budget)
- 4 blocks 9 (walk_to required for any realistic demo behavior)
- 5 blocks 9 (interact needed for motives to actually change)
- 6 blocks 9 if conversation assertions are in scope (check Open Q1 first)
- 7 does NOT block 9 for a 15-min demo (conversation won't exceed 40 turns); can land after
- 8 blocks 9's close-out (assertions need the harness)

External hard blockers (must close before item 1 starts): **reeims-221**, **reeims-2c1**, **reeims-c8c**.

## Cost envelope

- Implementation: ~5-8 worker dispatches + 1 veracity + 1 review. At sonnet rates, ~500k-800k tokens. Parent orchestration (sonnet) ~100-200k tokens. **~$10-25 to reach item 9.**
- Demo-run cost: ~$0.20 per 15-min demo (see Cost model).
- Iteration cost (tuning attention thresholds, prompt variants): budget 5-10 demo runs = ~$2.

## Success criteria (system level)

Demo (item 9) passes if:
1. Both Sims run 15 in-game minutes without crash, disconnect, or unresolved pathfind failure.
2. Each Sim makes 10-30 LLM turns (attention controller working).
3. At least one spontaneous conversation between Sims.
4. No god-mode leakage in agent logs (hygiene boundary holds).
5. Motive changes observed: at least one motive per Sim moves by ≥ 20 points during the run (agent actions affect world).
6. Human watching VNC sample reports "looks like living, not bot" in majority of 5-minute observations.

Failure modes and recovery:
- Sim stuck in a loop (same tool call 5x) → attention controller detects, widens triggers
- God-mode leakage detected → regression in PerceptionEmitter; roll back
- Conversation never happens → Open Q1 unresolved or wake trigger #6 broken
- Cost overrun → attention controller too permissive; tune thresholds, not model tier

## What this document is NOT

- A swarm-plan. `/swarm-plan` consumes this and produces the rd tree.
- A finalized spec. Open questions are real; resolve during planning or first implementation.
- A standalone roadmap. Three prerequisite items (reeims-221, -2c1, -c8c) must close first.
- A prose-narration layer proposal. That was v1 and was rejected as unnecessary overhead.
