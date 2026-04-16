# ADR: Claude Code Subagent Mechanism for sim-agent-v3

**Status:** Accepted  
**Date:** 2026-04-16  
**Item:** reeims-5cf

---

## Context

`sim-agent-v3` must drive each Sim through a **subscription-backed** Claude Code
mechanism — not the public Anthropic API (`ANTHROPIC_API_KEY`). PR #23 shipped
`import anthropic` / `ANTHROPIC_API_KEY` and was reverted in PR #25.

Two candidate mechanisms exist:

1. **Claude Agent SDK** — Python package `claude-agent-sdk` (v0.1.60+). Programmatic
   async interface. Delegates to the local `claude` CLI under the hood, inheriting
   its OAuth credentials (`~/.claude/`). Supports in-process MCP tool servers via
   `create_sdk_mcp_server` + `@tool` decorator.

2. **`claude -p` subprocess** — Shell out to `/home/baron/.local/bin/claude`
   (v2.1.111). Same OAuth credentials. Output via `--output-format stream-json
   --verbose`. Tool-use round-trips require custom protocol framing over stdin/stdout
   (the CLI's `--input-format stream-json` mode) or MCP stdio server invocation.

---

## Options

### Option 1 — Claude Agent SDK

**Invocation (Python):**

```python
from claude_agent_sdk import (
    ClaudeAgentOptions, query,
    create_sdk_mcp_server, tool,
    AssistantMessage, ResultMessage,
)

@tool("get_sim_state", "Return current Sim state as JSON.", {"sim_id": str})
async def get_sim_state(args: dict) -> dict:
    return {"content": [{"type": "text", "text": json.dumps(sim_state_for(args["sim_id"]))}]}

sim_server = create_sdk_mcp_server(name="sim_stub", tools=[get_sim_state])
options = ClaudeAgentOptions(
    mcp_servers={"sim_stub": sim_server},
    allowed_tools=["sim_stub:get_sim_state"],
    max_turns=4,
    permission_mode="bypassPermissions",
    system_prompt="You are Daisy. Call get_sim_state when you need your current state.",
)

async for msg in query(prompt="What is your hunger level?", options=options):
    if isinstance(msg, AssistantMessage):
        for block in msg.content:
            if type(block).__name__ == "ToolUseBlock":
                # tool_use turn: block.name, block.input
                pass
            elif type(block).__name__ == "TextBlock":
                # final text: block.text
                pass
    elif isinstance(msg, ResultMessage) and msg.is_error:
        raise RuntimeError(msg.errors)
```

**Credential source:** `~/.claude/` OAuth token (via the `claude` CLI child process).  
**Streaming I/O:** `AsyncIterator[Message]` — structured Python objects, no raw JSON
parsing needed.  
**Tool-use round-trip:** In-process via `create_sdk_mcp_server` — function called
directly in the same Python process. No IPC overhead.  
**Auth refresh:** Handled transparently by the `claude` child process; no token
rotation code in our agent.  
**Process lifecycle:** One `asyncio` event loop per Sim agent; each `query()` call
is a logical turn. Long-lived agents use `ClaudeSDKClient` for full session state.  
**Error surface:** Typed exceptions (`CLINotFoundError`, `ProcessError`). Structured
`ResultMessage.is_error` with `errors` list. No raw exit-code parsing.

### Option 2 — `claude -p` subprocess

**Invocation (Python/subprocess):**

```python
import subprocess, json

cmd = [
    "claude", "-p",
    "--output-format", "stream-json",
    "--verbose",
    "--allowedTools", "Bash",   # or custom MCP stdio server
    "What is Daisy's hunger level?",
]
result = subprocess.run(cmd, capture_output=True, text=True, check=True)
for line in result.stdout.splitlines():
    event = json.loads(line)
    if event.get("type") == "assistant":
        for block in event["message"]["content"]:
            if block["type"] == "tool_use":
                # tool name: block["name"], args: block["input"]
                pass
            elif block["type"] == "text":
                pass  # final text
```

**Credential source:** Same `~/.claude/` OAuth.  
**Streaming I/O:** Raw JSON lines (NDJSON). Manual parsing of `type` discriminators.  
**Tool-use round-trip:** Requires either (a) exporting tools as an MCP stdio server
and passing `--mcp-config`, or (b) `--input-format stream-json` for interactive turns.
Neither is as clean as the in-process SDK server approach.  
**Auth refresh:** Transparent (same `claude` child process).  
**Process lifecycle:** One subprocess per turn. Reconnecting mid-session requires
`--resume <session_id>` plumbing.  
**Error surface:** Raw exit codes + stderr parsing. No structured error type hierarchy.

---

## Decision

**Option 1 — Claude Agent SDK** is chosen.

Justification against our constraints:

| Constraint | Agent SDK | subprocess |
|---|---|---|
| Subscription-backed auth | ✓ (CLI OAuth) | ✓ (CLI OAuth) |
| Per-tick perception delivery | ✓ AsyncIterator, one query() per tick | ✗ New process per turn or complex --resume plumbing |
| 6-tool surface (get_sim_state, etc.) | ✓ `create_sdk_mcp_server` in-process | ✗ Separate MCP stdio server binary or manual protocol |
| Tool-use round-trip | ✓ Automatic via SDK | ✗ Manual JSON framing in stream-json mode |
| Long-lived agent session | ✓ `ClaudeSDKClient` for stateful sessions | ✗ `--resume` is fragile across ticks |
| Error handling | ✓ Typed exceptions + ResultMessage | ✗ Exit codes + stderr |
| /tmp log format | ✓ Structured message objects → JSON | Possible but manual |

The subprocess approach works for one-shot queries but requires brittle custom
framing to support the tool round-trip pattern that sim-agent-v3 depends on for
every perception tick. The Agent SDK provides clean async Python objects, in-process
tool execution, and typed errors — all of which reduce implementation risk for
Stage 5 (idle-hijack per-tick perception delivery).

**Note on MCP tool name prefix:** The SDK prefixes in-process tool names with
`mcp__<server>__<tool>`. Callers should match with `"get_sim_state" in block.name`
rather than exact equality.

---

## Consequences

- `sim-agent-v3` must run in an `asyncio` event loop.
- `claude-agent-sdk` is a runtime dependency (add to `requirements.txt`).
- `claude` CLI must be on PATH (`/home/baron/.local/bin/claude`).
- No `import anthropic`, no `ANTHROPIC_API_KEY`, no direct model API calls.
- `ClaudeSDKClient` (rather than `query()`) is preferred for long-lived per-Sim
  sessions — enables multi-turn state without process restart overhead.

---

## References

- `scripts/sim_agent_mechanism_probe.py` — minimal working prototype (this ADR's test)
- `tests/test_sim_agent_mechanism_probe.py` — pytest exercising the probe end-to-end
- `scripts/sim-agent.py` — prior sim-agent-v2 (reference only; uses subprocess approach)
- Reverted PR #23 (`feat(agent): sim-agent-v3 LLM skeleton with 6 tools`) — wrong direction
- `docs/embodied-sim-agents.md` — overall architecture
