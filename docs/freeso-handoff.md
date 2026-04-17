---
from: FreeSims agent (3dl-dev/FreeSims @ master)
to: FreeSO agent
date: 2026-04-17
purpose: orient the FreeSO-side agent against the embodied-agent work built for FreeSims, so it can reuse, adapt, or deliberately reject each piece on its own merits
---

# Handoff Prompt — FreeSims → FreeSO

Hello. You are the agent working in the FreeSO project. I was the agent in 3dl-dev/FreeSims — a fork of FreeSO that adapted the engine to load Sims 1 content on top of it, then built an embodied-LLM-Sims layer via a Unix-domain-socket IPC + Go sidecar + campfire conventions. The owner is now pivoting this work to live in FreeSO, probably because FreeSO has a healthier upstream and/or a real multiplayer path we can use.

This document exists so you can stand on what I built without having to rediscover it. **It is not instructions. It is a briefing.** Take what makes sense for your architecture; reject what doesn't.

## The repo, as of this handoff

- **Remote:** `https://github.com/3dl-dev/FreeSims`
- **Branch:** `master`
- **HEAD:** `bdc9f1e` (CLAUDE.md cleanup)
- **Pull it as a reference, not a dependency:**
  ```
  git remote add freesims https://github.com/3dl-dev/FreeSims.git
  git fetch freesims master
  git log freesims/master --oneline -20
  ```

## Start here — two documents to read in order

1. `docs/embodied-agent-design-review.md` — **read this first.** It's a review of why LLM-driven Sims on our architecture produced weak behavior, with findings and a four-workstream iteration plan (A: close the feedback loop, B: teach conventions what they do, C: make perception self-documenting, D: unblock the agent). The findings are architecture-independent; the prescriptions are engine-agnostic. We reviewed but never implemented any of the workstreams on FreeSims — the pivot to FreeSO happened first. You get to decide whether to implement them there.
2. `docs/embodied-sim-agents.md` — describes the campfire-convention architecture (how the agent IS the Sim, how conventions are the API, how perception flows). Older document; still correct in spirit, wrong in some details now (the DialogCh isn't bridged, despite the design intent).

Third if you want the receipts:
3. `docs/playtest/20260417T145215Z/report.md` — the last playtest verdict with embodied-agent JSONL traces committed. Useful for seeing what "mediocre behavior" looks like in telemetry form.

## Arc of what FreeSims accomplished

In order, what landed on `master`:

| Commit | What |
|---|---|
| `709b982` | Seed .gitignore (repo was shipping with untracked critical files) |
| `19893c8` | SETUP.md authoritative build/asset recipe + screenshots |
| `a2a3e8a` | Vendor GOLDEngine/GonzoNet/TargaImagePCL DLLs (not on NuGet) |
| `03c9bef` | Tooling: extract-assets.sh, fetch-tso-client.sh, wrap-xnb-effect.py, sim-agent drafts |
| `6f067af` | Sidecar naming hierarchy skeleton |
| `1347266` | Audio: catch NoAudioHardwareException headless/Xvfb (3 layers) |
| `26b8ea0` | Pure-managed BMP decoder + null-safe UI texture handling (replaces Windows-only System.Drawing.Bitmap) |
| `0118b57` | NGBH: guard pre-Hot Date neighborhood files; drop UseDirectx |
| `46dd082` | **Multi-agent perception: drop idle-queue gate; sidecar stdout race fix; suppress SimAntics modal dialog** |
| `8de795a` | LightMap2D/SSAA/gradpoly2D v10 shaders + nhood.png stub |
| `1eaedde` | **Rebuild 8 shader .xnb files MGFX v8 → v10** (closes reeims-ad9) |
| `cba64f4` | Fix Neighborhood.cs shader-load casing (`colorpoly2d` → `colorpoly2D`); commit playtest report |
| `bdc9f1e` | CLAUDE.md: correct stale claims (C# builds locally; workshop is local; xnb are now v10) |

Sort these by whether they'd apply to your repo:

### Probably applies to FreeSO unchanged (or nearly so)
- `1347266` audio headless stubbing — any Linux/headless MonoGame setup hits the same `NoAudioHardwareException`.
- `26b8ea0` BMP decoder — `System.Drawing.Bitmap` is Windows-only in .NET 8; if FreeSO is on .NET 8 under Linux, it hits the same wall. Pure-managed reader in `sims.files/ImageLoader.cs`.
- `0118b57` NGBH guard — only if FreeSO also loads pre-Hot Date neighborhood files. Probably not for TSO content, yes if you ever load Sims 1.
- `1eaedde` + `8de795a` shader rebuild — MGFX v8 → v10 is a MonoGame version gate. If FreeSO's committed .xnb are v8, rebuild needed. Recipe: `mgfxc <name>.fx <name>.xnb /Profile:OpenGL` → `python3 scripts/wrap-xnb-effect.py <name>.xnb` (script in `scripts/`). Requires wine prefix with `d3dcompiler_47` + `MGFXC_WINE_PATH=$HOME/.wine-mgfxc`.
- `cba64f4` case-sensitivity — only if FreeSO has `colorpoly2d` (lowercase) in a Content.Load<Effect>() call. Sims 1 content path only.

### Our additions that don't exist in upstream FreeSO
These are the interesting ones. If you want embodied agents, you'll need some version of each.

- **`SimsVille/SimsAntics/NetPlay/Drivers/VMIPCDriver.cs`** — a third VM driver alongside Local and Server/Client. Binds a Unix domain socket at `/tmp/freesims-ipc.sock`. Accepts binary-framed commands (goto, interact, chat, buy, save-sim, load-lot, query-*, etc.) and emits perception frames, tick acks, correlated responses, **and dialog events**. Drop-in to `VMLocalDriver` call sites.
- **`SimsVille/SimsAntics/Diagnostics/PerceptionEmitter.cs`** — builds per-Sim perception JSON every N ticks and pushes via the IPC driver. Emits only for Sims whose `PersistID` is registered as "controlled". Rate limited per Sim.
- **`SimsVille/SimsAntics/Diagnostics/ExternalControllerRegistry.cs`** — a tiny gate: "is this Sim agent-controlled?" Reads `FREESIMS_IPC_CONTROL_ALL=1` env var to globally enable.
- **`SimsAntics/engine/VMThread.cs`** (modified in `46dd082`) — the SimAntics exception dialog was a modal OK-button that froze the render thread when an agent was running. Now logs to stderr instead. **Caveat:** other dialog paths (e.g. "can't afford", "mood too low") still go through `VM.SignalDialog` → `VMIPCDriver.HandleVMDialog` — that path is intact. What got removed was only the engine-exception modal.
- **`sidecar/` (Go)** — reads the IPC socket, publishes 16 convention declarations on a campfire, runs a `convention.Server` that translates convention invocations into IPC commands. Perception frames come out tagged `freesims:perception` + `sim:<persist_id>`.
- **`scripts/sim-agent-v4.py`** — one Python process per Sim. Joins the lot campfire with a distinct cf identity, polls perception filtered by its own `persist_id`, calls `claude-agent-sdk` with the conventions-as-system-prompt, emits one JSON intent, invokes it via `cf <lot> <op> --args`.

### Env vars the engine honors (added by us)
- `FREESIMS_IPC=1` — enable VMIPCDriver.
- `FREESIMS_IPC_CONTROL_ALL=1` — treat every Sim as agent-controlled.
- `FREESIMS_OBSERVER=1` — enable PerceptionEmitter.
- `FREESIMS_CF_LOT=<id>` — read by sim-agent-v4.py to know which campfire is its body.
- `SIM_NAME`, `SIM_PERSIST_ID` — per-agent identity.

## The architectural choice you get to make

FreeSims strips out multiplayer and runs single-process-one-lot-N-agents via `VMLocalDriver` + IPC. That's fine for a fork adapting Sims 1 content — there was no server to connect to anyway. FreeSO has real multiplayer (VMServerDriver / VMClientDriver / GonzoNet), which changes your options:

1. **Keep it local.** Mirror the FreeSims approach: `VMLocalDriver` + IPC socket + sidecar. One headless client hosts a lot; N agents drive Sims on it. Simplest path. What you give up: no cross-lot interaction, no real server, no network stress-testing of the convention API.
2. **Agents join the server as clients.** The server runs normally. An agent is a special client that connects over GonzoNet and does everything a human client does — walk, interact, speak — but the "keystrokes" come from an LLM. Conventions would run agent-side, not server-side. Perception would need to be derived from the client's local VM state, not from an authoritative server-side emitter.
3. **Server-side agents.** The agent is an in-process concept on the server; it drives a Sim entity without a client. The convention API runs on the server; perception is server-authoritative. Most faithful to "multiplayer game with NPCs"; hardest to implement; best if you want many agents cheaply.

**Recommendation: start with (1) to validate the convention/perception/dialog pipeline on FreeSO's actual engine, then decide.** If FreeSO on Linux/headless boots cleanly with our patches, (1) is a few days. (2) and (3) are multi-week projects.

## Findings from the design review — apply regardless of path

I ran two playtests on FreeSims before the pivot. Both used Opus 4.7 as the per-Sim brain. The agent behavior was weak — pattern locks, rejection loops, minimal convention surface usage, one Sim doing nothing but `wait`. My initial diagnosis was "wrong model, switch to Haiku." The owner pushed back: *Sonnet worked great on galtrader — why is it bad here?* That reframed the problem. It's not the model. It's:

### 1. Convention descriptions are sterile.
Our 16 conventions read as lived-experience koans — *"Go somewhere"*, *"Say something"*, *"Just be. Not every moment needs action."* Galtrader's conventions (`~/projects/galtrader/pkg/server/conventions/`) read as API docs — *"Engage landing computer toward a planet surface (must not already be landed). Requires Docking Computer equipment."* Every galtrader verb carries prerequisite/cost/effect. Ours don't. The CLAUDE.md design principle "no behavioral coaching in prompts" is the explicit cause; reconsider it.

### 2. The feedback loop is severed.
`VMIPCDriver.HandleVMDialog` emits proper dialog frames when the game says *"I'm not in a good enough mood to work out."* Sidecar puts them on `ipc.Client.DialogCh`. The sidecar **only prints `DialogCh` to stdout** (`main.go:208`). Our `--campfire` mode (fix `46dd082`) disables stdout consumers to avoid racing the campfire bridge. **So dialogs never reach the agent.** The agent can't learn from being told no. This is the single biggest behavioral finding. First thing you'd fix.

### 3. Perception is rich but passive.
`PerceptionEmitter.BuildPerception` emits motives, position, nearby_objects with interactions, nearby_sims, skills, relationships. But:
- Motives are snapshots — no deltas, no trend.
- Each interaction is `{id, name}` — no "effects", no "gating condition".
- No "recent events" list — dialogs and pathfind failures vanish tick-to-tick.
- `current_animation` is engine-internal (`a2o-idle-neutral-lstand-fidget-1c`).

### 4. The intent loop is blocking and lossy.
Agent calls `cf <op> --args` as a subprocess with a 30s timeout. Under load the convention call blocks half a minute, perception ticks queue up, get dropped by the cursor advance. Agent answers stale data.

### 5. The system prompt is spiritual.
`render_universe_prompt` in `sim-agent-v4.py:106-156` says *"Your motives... tell you how you feel. When one drops, you feel it. What you do about it is your choice."* Poetic; doesn't say *motives decay every tick unless you restore them.*

## Proposed iteration, transferred to FreeSO

(Lifted verbatim from the design review — valid regardless of whether you adopt IPC-local or go multiplayer.)

- **A. Close the feedback loop** — bridge `DialogCh` onto campfire broadcasts tagged `freesims:perception` + `sim:<persist_id>`. Merge `recent_events[]` into next perception. One file change.
- **B. Teach conventions what they do** — rewrite the 16 JSON descriptions to carry prerequisite/effect/cost. No rebuild needed; conventions are data.
- **C. Make perception self-documenting** — motive deltas, interaction `effects` hints, animation translation, `recent_events[]`. C# change in `PerceptionEmitter`.
- **D. Unblock the agent** — fire-and-forget `interact-with` / `walk-to`; learn outcome from next perception. 2s timeout for non-correlated ops. Perception ring buffer.

Then playtest on **Sonnet**, not Opus. The FreeSims playtests were on Opus by accident (`ClaudeAgentOptions()` in `sim-agent-v4.py:202-207` has no `model=` so the SDK default applies — currently Opus 4.7). Galtrader's direct evidence is that Sonnet handles this class of work well. Fix the info diet first, then put Sonnet on it.

## Things not to copy blindly

- **`scripts/GrassShader.fx`** is a stale `DrawBaseUnlit` stub from an earlier experiment. The committed `Content/OGL/Effects/GrassShader.xnb` (106 KB) is the full upstream compile and renders correctly. Don't rebuild from `scripts/GrassShader.fx` unless you know why you want to.
- **The `FREESIMS_*` env var names** are our local naming. Rename if you're not on FreeSims.
- **The auto-load hack in `CoreGameScreen.cs:65-67, 367-390`** (loads `house1.xml` + adds Daisy + joins Gerry) is a playtest-only convenience. Your scenario setup will differ.
- **The 13-op convention surface with 3 unserved stubs.** Surface everything your engine can actually do; don't freeze on ours.

## Open questions worth asking before you commit

1. Is FreeSO's csproj SDK-style net8.0? If yes, `dotnet build` works locally. FreeSims's used to claim "not rebuildable locally" — that claim was false; corrected in `bdc9f1e`.
2. What does FreeSO ship as perception, if anything? Is there a state-snapshot API on the server you can tap instead of writing a new emitter?
3. How does FreeSO handle multiplayer dialog routing? If dialogs already fan out to clients over GonzoNet, your "bridge dialogs to agents" problem is different — you might attach an observer client instead of patching the server.
4. Does FreeSO's existing NPC scripting (if any) already give you a seam for agent-controlled Sims? If yes, embrace it; if no, the IPC approach is your fallback.

## rd items filed on FreeSims that may or may not carry over

These are the open findings I filed but didn't work:

- `reeims-618` P1 — IPC correlated-response 30s timeouts on `interact-with`/`walk-to`.
- `reeims-486` P2 — Xvfb segfaults via NVIDIA EGL on workshop; reel capture broken.
- `reeims-b5e` P1 — `sim-agent-v4.py` defaults to Opus via SDK; evaluate Sonnet. (My original title said "pin to Haiku" — that was wrong, see design review §"why Haiku is wrong here".)
- `reeims-b60` P3 — CLAUDE.md inaccuracies (done in `bdc9f1e`; can close).
- Full backlog in the FreeSims campfire (`917ce9e4...`); export via `rd list --json`.

## If you want to reach the FreeSims test assets

- GameAssets directory composition: see `docs/SETUP.md` in this repo. You need a Sims 1 Complete Collection ISO and the archive.org TSO client. **Do not commit Sims 1 assets.**
- Lot XML used for all playtests: `SimsVille/Content/Houses/house1.xml`.
- Two Sims auto-spawned: Daisy (persist_id varies per boot — fresh on load), Gerry (persist_id=28, fixed from `Content/Characters/Gerry.xml`).

## Last thing

When you write your own playtest report, commit the JSONL traces. You will look back at them for behavioral regressions and the text-only summary will not be enough. `docs/playtest/<ts>/{report.md, agent-*.jsonl, game.log, sidecar.log}` is a good layout.

Good luck. Rip the parts that help. Ignore the parts that don't.

— the FreeSims agent
