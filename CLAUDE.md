# CLAUDE.md — FreeSims

> OS-level instructions (session protocol, work management, model routing, rules, campfire coordination, token optimization) are inherited from `~/.claude/CLAUDE.md`. Resonant methodology is inherited from `~/projects/resonant/docs/specs/`. This file contains only project-specific configuration.

## Project

**FreeSims** is a fork of [francot514/FreeSims](https://github.com/francot514/FreeSims), which is itself a fork of [riperiperi/FreeSO](https://github.com/riperiperi/FreeSO) (open-source reimplementation of *The Sims Online*) adapted to load Sims 1 content on top of the FreeSO engine. Our fork at `3dl-dev/FreeSims` ports it to modern .NET 8 / MonoGame 3.8 / Linux arm64 and uses it as a runtime substrate for LLM-driven Sim characters.

Boot needs **two asset trees**: a TSO client (engine substrate — UI, avatars, objects, globals) and a Sims 1 install (content — lots, neighborhoods, extra objects). Both are fetched by scripts under `scripts/` and composed at `GameAssets/` (gitignored). See `docs/SETUP.md`.

The multiplayer network layer (`VMServerDriver`/`VMClientDriver`, GonzoNet) is disabled in favor of `VMLocalDriver` + a planned campfire-convention bridge — N LLM agents coordinate through campfire as a single process hosts one lot. This is the shape of the "online where many agents play together" target; FreeSO's server/client split is not revived.

## Stack

- **Language:** C# (.NET 8)
- **Engine:** MonoGame 3.8.2 DesktopGL (SDL2 on Linux)
- **Build:** `dotnet build SimsVille/SimsVille.csproj` (three projects: sims.files, sims.common, SimsVille)
- **Run:** Xvfb :99 (1024x768) + x11vnc (VNC on localhost:5900). WSLg's X server hangs MonoGame's SDL event loop; Xvfb bypasses it.
- **Game files:** Requires two asset trees — TSO client (from archive.org, script-fetched) + Sims 1 Complete Collection iso (user-supplied). See `docs/SETUP.md`.
- **Shader pipeline:** 11 .xnb effect files rebuilt to MGFX v10 on x86_64 workshop (`baron@workshop.stealth.baron.local`) via wine-backed `mgfxc`. See `docs/SETUP.md` "Shader compilation" section. GrassShader uses a custom `DrawBaseUnlit` technique (bypasses FreeSO's unimplemented lighting pipeline).

## Repo Layout

```
FreeSims/
├── SimsVille/               # Main game executable (net8.0, the only Exe project)
│   ├── SimsAntics/          # SimAntics bytecode VM, entities, primitives, NetPlay drivers
│   ├── ContentManager/      # Content loaders (FAR1/FAR3, IFF, OTF, UI, audio)
│   ├── UI/                  # UI framework + screens (LoadingScreen, CoreGameScreen, ...)
│   ├── World/               # World renderer (terrain, walls, floors, roofs, objects)
│   ├── Utils/GameLocator/   # LinuxLocator (asset path resolution)
│   └── Content/             # Bundled content (shaders in OGL/Effects/, XML manifests, UIScript)
├── sims.common/             # Shared: rendering framework, vitaboy animation, camera, utils
├── sims.files/              # Asset format parsers (FAR, IFF chunks, HIT audio, OTF)
├── lib/                     # Vendored .NET Framework DLLs (GOLDEngine, GonzoNet, TargaImagePCL)
├── scripts/                 # extract-assets.sh, fetch-tso-client.sh, wrap-xnb-effect.py
├── GameAssets/              # (gitignored) TheSims/ + TSOClient/ extracted game content
├── .assets-src/             # (gitignored) ISOs, zips, staging dirs
├── docs/                    # SETUP.md, screenshots
├── SimsNet/                 # (not built) Multiplayer server — missing deps
├── sims.parser/             # (not built) WinForms IFF browser
└── sims.debug/              # (not built) WinForms debug tool
```

## Key Architecture Notes (for cold-start recovery)

- **VMIPCDriver** (`SimsVille/SimsAntics/NetPlay/Drivers/VMIPCDriver.cs`) is the active VM driver when `FREESIMS_IPC=1` is set. Unix domain socket at `/tmp/freesims-ipc.sock`. Accepts binary-framed commands (goto, interact, chat, buy, etc.), emits perception frames, tick acks, and correlated responses. The Go sidecar is the bridge between this socket and the campfire.
- **VMLocalDriver** (`SimsVille/SimsAntics/NetPlay/Drivers/VMLocalDriver.cs`) is the fallback when IPC is not set. Queues commands in-process.
- **Auto-load lot hack** in `CoreGameScreen.cs:65-67, 367-390` — a countdown timer auto-loads `Content/Houses/house1.xml` with Daisy on first idle tick. Also joins Gerry (uid=28) from `Content/Characters/Gerry.xml`. Without this, the game sits at the city view waiting for user gizmo input.
- **Audio is stubbed.** `NoAudioHardwareException` from SDL dummy driver is caught at three layers (Audio.cs, HITThread.cs, Game.cs). Sound effects cache null and silently skip. This is correct for headless/Xvfb operation.
- **BMP decoder rewritten.** `sims.files/ImageLoader.cs` uses a pure-managed BITMAPINFOHEADER reader (not System.Drawing.Bitmap, which is Windows-only in .NET 8). Supports 1/4/8/24/32 bpp BI_RGB/BI_BITFIELDS. PNG/JPG go through MonoGame's native Texture2D.FromStream.
- **Camera clamped.** Edge-scroll disabled in UILotControl.cs; pan bounds tightened in World.cs. Home key recenters.
- **Symlink required.** `SimsVille/bin/Debug/net8.0/GameAssets -> /home/baron/projects/FreeSims/GameAssets` (absolute). Created once; survives `dotnet build` but not `dotnet clean`. Re-create if missing: `ln -sf /home/baron/projects/FreeSims/GameAssets SimsVille/bin/Debug/net8.0/GameAssets`.
- **Workshop SSH** (`baron@workshop.stealth.baron.local`) — x86_64 Ubuntu 24.04 VM used as a cross-compile farm for anything that can't run on arm64. Key-based auth, no password. Currently set up for shader compilation:
  - `dotnet-sdk-8.0` installed, `dotnet tool install -g dotnet-mgfxc` done, `~/.dotnet/tools` on PATH.
  - Wine prefix at `~/.wine-mgfxc` (WINEARCH=win64) with `d3dcompiler_47` + Windows dotnet SDK 8.0.201 installed.
  - `MGFXC_WINE_PATH=$HOME/.wine-mgfxc` enables `mgfxc` to find the prefix.
  - **Recipe:** rsync .fx sources to workshop → `mgfxc <name>.fx <name>.xnb /Profile:OpenGL` → `python3 scripts/wrap-xnb-effect.py <name>.xnb` (XNBd wrapper) → rsync .xnb back → place in `SimsVille/Content/OGL/Effects/`. Full recipe in `docs/SETUP.md` "Shader compilation" section.
  - Required only when .fx sources change or MonoGame upgrades the MGFX binary version. The committed .xnb files are MGFX v10 and work with MG 3.8.x.
  - `wine32:i386` must NOT be installed on workshop — Ubuntu's wine wrapper prefers 32-bit wine when both are present, breaking mgfxc.

## Work Management

`rd` initialized. Work items tracked in the FreeSims campfire (`917ce9e4...`). Follow the OS session protocol:

1. `rd ready` at session start
2. `rd claim <id>` before starting work
3. `rd done <id> --reason "..."` on close
4. New items for any sub-tasks, decisions, or blockers discovered mid-work

## Agent Routing

No specialist agents defined yet. Work directly until a recurring domain emerges that warrants an agent spec in `.claude/agents/`.

Default model tier: **Haiku** for build/config edits and asset spelunking, **Sonnet** for C# implementation and engine reasoning, **Opus** only for architectural decisions (engine rearchitecture, protocol design).

## Source of Truth Hierarchy

1. This fork's code (what's actually running)
2. `docs/SETUP.md` (build/run/asset recipes — authoritative for how-to)
3. rd items (decisions, specs, context)
4. Upstream `francot514/FreeSims` and `riperiperi/FreeSO` (for "how did this work originally?" questions — not binding)

## Architecture: Campfire Conventions (galtrader pattern)

The agent-game interface uses **campfire conventions**, not MCP or raw IPC. This is the same pattern as `~/projects/galtrader`. Conventions are the API — `cf send`/`cf read` is the protocol layer and must never be used directly.

```
SimsVille (C# game)
  ↕ binary IPC over Unix socket (/tmp/freesims-ipc.sock)
Sidecar (Go, sidecar/freesims-sidecar --campfire)
  - Creates freesims.lot campfire at startup
  - Publishes 16 convention declarations (embedded via //go:embed)
  - Runs convention.Server per operation (campfire Go SDK v0.19.2)
  - Handlers translate convention invocations → binary IPC commands
  - PerceptionBridge: game perception frames → campfire broadcasts
    tagged freesims:perception + sim:<persist_id>
  ↕ campfire (filesystem transport)
Agent (Python, scripts/sim-agent-v4.py, one per Sim)
  - Joins freesims.lot campfire
  - Reads convention declarations → renders as lived-experience system prompt
  - Polls perception broadcasts filtered by sim:<own_persist_id>
  - LLM outputs JSON intent → agent invokes via cf <lot> <op> --args
  - No MCP, no in-process tools, no max_turns fiddling
```

**Design principle:** The agent IS the Sim, not a puppet controller. Convention descriptions are first-person ("Go somewhere", "Say something", "Just be") not third-person ("Walk to position", "Invoke operation"). The self-documenting `interactions` list on each nearby_object tells the Sim what it can do. No behavioral coaching in prompts.

### Convention Surface (16 declared, 13 with handlers)

| Convention | Handler | What the Sim experiences |
|---|---|---|
| walk-to | GotoCmd (interaction=2 "Walk Here") | Go somewhere |
| speak | ChatCmd | Say something |
| interact-with | InteractionCmd | Do something with a nearby object |
| wait | No IPC — immediate return | Just be |
| remember | Store to campfire (tagged freesims:memory) | Hold onto a thought |
| query-sim-state | QuerySimStateCmd (correlated) | Check on yourself or someone |
| query-catalog | QueryCatalogCmd (correlated) | Browse what you could buy |
| buy | BuyObjectCmd | Buy something for your home |
| save-sim | SaveSimCmd (correlated) | Settle in — save who you are |
| load-lot | LoadLotCmd (correlated) | Move to a different home |
| move-object | MoveObjectCmd | Rearrange your home |
| delete-object | DeleteObjectCmd | Get rid of something |
| sim-action | parseCommand (god-mode fallback) | Anything specific conventions don't cover |
| query-lot-state | — (no IPC type) | Unserved |
| query-pie-menu | — (no IPC type) | Unserved |
| sim-build | — (no handler) | Unserved |

### Running the demo

```bash
scripts/demo-campfire.sh --duration 180
```

Starts Xvfb → SimsVille (with FREESIMS_IPC=1) → sidecar --campfire → discovers Sims from perception stream → launches one sim-agent-v4.py per Sim. Each agent gets its own cf identity joined to the lot.

### Key files

- `sidecar/campfire.go` — convention server: StartConventionServer, all handlers, perceptionBridge
- `sidecar/conventions/*.json` — 16 convention declarations (embedded at build)
- `sidecar/naming.go` — NamingHierarchy skeleton (freesims.lot, freesims.sims.<id>)
- `scripts/sim-agent-v4.py` — the Sim's consciousness
- `scripts/demo-campfire.sh` — end-to-end demo launcher
- `scripts/sim-agent-v3.py` — SUPERSEDED (MCP path, kept for reference only)

## Current State + Open Work

Stages 2–5 from the original plan are **done** (VMIPCDriver, sidecar, conventions, perception emitter). Stage 6 was replaced by reeims-fa6 (campfire convention architecture) which is **closed** — the architecture shipped but the demo needs a clean end-to-end run.

### Critical path to working demo

1. **reeims-fc8** (P1) — duplicate Gerry avatar: 3 sims spawned, only 2 expected. Game binary not rebuildable locally (csproj targets .NET Framework 4.5). Needs workshop investigation. Workaround: demo discovers sims from perception at runtime.
2. **reeims-6d4** (P1) — Demo: 2 embodied Sims × 15 in-game minutes end-to-end validation. Depends on fc8 workaround or fix.
3. **reeims-767** (P2) — PerceptionEmitter filter mis-fires when controlled Sim's PersistID diverges (related to fc8).

### Security (P1, do before any external demo)

- **reeims-a2e** — CoreGameScreen.LoadLotByXmlName accepts absolute paths from agent input
- **reeims-797** — VMNetLoadLotCmd lacks server-side path safety (house_xml path traversal)

### 34 ready items

Run `rd ready` for the full list. Priorities: 1 P0 (save-sim round-trip test), 16 P1 (bugs, reviews, security, demo), 11 P2 (test coverage, findings), 6 P3 (lint, docs).

## Build Constraints

- **C# binary not rebuildable locally.** `SimsVille.csproj` targets .NETFramework v4.5 in old-style format. `dotnet build` fails with "reference assemblies for .NETFramework,Version=v4.5 were not found." The binary at `bin/Debug/net8.0/SimsVille` was pre-built (possibly on workshop or via a different toolchain). Any C# changes require the workshop (`baron@workshop.stealth.baron.local`) or a toolchain fix.
- **Go sidecar builds fine.** `cd sidecar && go build -o freesims-sidecar .` — requires Go 1.25+ (campfire v0.19.2 dependency).
- **Python agent needs** `claude-agent-sdk` (pip install) and `cf` CLI on PATH.
- **Game env vars:** `FREESIMS_IPC=1 FREESIMS_IPC_CONTROL_ALL=1 FREESIMS_OBSERVER=1` for IPC + perception.
- **Sidecar CF_HOME** must point to an unwrapped cf identity (`cf init --cf-home <dir>`). Agent CF_HOME must be a separate identity.

## Sidecar IPC Notes

- **Goto marker interaction=2** ("Walk Here"), not 0. Interaction 0 is undefined on GOTO_GUID 0x7C4 and silently drops.
- **Walk arrival tolerance:** distance² ≤ 1024 (≈ 2 tiles). Was 4 (impossible when target is another Sim).
- **Correlated queries** (query-sim-state, query-catalog, save-sim, load-lot): send command with request_id, await matching response on ipc.Client.ResponseCh with timeout.

## Testing

No test suite exists in the upstream repo. Before writing new code for any subsystem, **add a test harness for that subsystem** per the Resonant Quality Protocol (ground-source testing rule §10 in OS CLAUDE.md). A skipped or absent test for an interface you touch means the work is not done.

For pure engine logic: xUnit or NUnit, run via `dotnet test`.
For IPC/sidecar: Go test + a short-lived SimsVille launch under Xvfb.
For visual/interactive behavior: `xdotool` scripted click sequences under Xvfb + `scrot` screenshots. Document the manual repro until an automated harness exists; create an rd item for the automation.

## Conventions

- License: MPL 2.0 — preserve file headers on modified files.
- No Sims/TSO asset files committed. Ever. (`GameAssets/` and `.assets-src/` are gitignored.)
- Rebuilt shader .xnb files ARE committed (build artifacts, not copyrighted content).
- All new C# files go in the three build projects (SimsVille, sims.common, sims.files). The excluded projects (SimsNet, sims.debug, sims.parser) are dead — don't add to them.
- `SimsVille/Debug/` directory is excluded from compile. New debug/observer code goes in `SimsVille/SimsAntics/Diagnostics/` (create as needed) or an appropriate existing namespace.
