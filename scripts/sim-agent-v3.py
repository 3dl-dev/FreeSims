#!/usr/bin/env python3
"""sim-agent-v3.py — Embodied Sim agent via Claude Agent SDK.

Reads perception events (JSONL) from stdin (same sidecar protocol as v2).
Each perception triggers _call_llm() only when should_think() fires one of 8
wake triggers (attention controller). Reduces ~600 LLM calls/10-min to ≤30.

Env: SIM_NAME, PERSIST_ID
Log: /tmp/embodied-agent-<persist_id>.jsonl
"""

import asyncio
import json
import os
import shutil
import sys
from dataclasses import dataclass, field
from datetime import datetime, timezone


# ---------------------------------------------------------------------------
# Attention controller — intent state + 8 wake triggers
# ---------------------------------------------------------------------------

MOTIVE_DELTA_THRESHOLD = 10   # trigger 1
MOTIVE_LOW_THRESHOLD = 20     # trigger 2
PERIODIC_TICK_INTERVAL = 60   # trigger 8


@dataclass
class IntentState:
    """Agent intent between LLM calls. Suppresses redundant wakes."""
    goal: str = ""; started_at: int = 0; wait_until_tick: int = 0
    last_llm_tick: int = -PERIODIC_TICK_INTERVAL  # primed: fires on tick 1
    last_motives: dict = field(default_factory=dict)
    last_lot_avatars: list = field(default_factory=list)


def _motive_keys(p: dict) -> dict: return p.get("motives") or {}
def _avatar_names(p: dict) -> list: return sorted(a.get("name", "") for a in (p.get("lot_avatars") or []))
def _events(p: dict) -> list: return p.get("recent_events") or []


def should_think(perception: dict, state: IntentState, tick: int) -> tuple[bool, str]:
    """Pure function — returns (True, trigger_tag) if any of 8 wake triggers fire."""
    sim_name = perception.get("name", "").lower()
    motives = _motive_keys(perception)
    avatars = _avatar_names(perception)
    evts = _events(perception)

    if state.last_motives:
        # T1: motive delta
        for k, v in motives.items():
            old = state.last_motives.get(k)
            if old is not None and abs(v - old) >= MOTIVE_DELTA_THRESHOLD:
                return True, "motive_delta"
        # T2: threshold cross
        for k, v in motives.items():
            old = state.last_motives.get(k)
            if old is not None and old >= MOTIVE_LOW_THRESHOLD > v:
                return True, "motive_threshold"

    # T3: avatar list changed
    if avatars != state.last_lot_avatars:
        return True, "avatar_change"

    for ev in evts:
        if not isinstance(ev, dict):
            continue
        t = ev.get("type")
        # T4: chat_received event
        if t == "chat_received":
            return True, "chat_received"
        # T5: pathfind-failed event
        if t == "pathfind-failed":
            return True, "pathfind_failed"

    # T6: direct address (name in chat text)
    if sim_name:
        for ev in evts:
            if isinstance(ev, dict) and ev.get("type") == "chat_received":
                if sim_name in ev.get("text", "").lower():
                    return True, "direct_address"

    # T7: intent timer elapsed
    if state.wait_until_tick > 0 and tick >= state.wait_until_tick:
        return True, "intent_timer"

    # T8: periodic fallback
    if (tick - state.last_llm_tick) >= PERIODIC_TICK_INTERVAL:
        return True, "periodic"

    return False, "suppressed"


# ---------------------------------------------------------------------------
# Prerequisite check — MUST NOT import anthropic
# ---------------------------------------------------------------------------

def _check_prerequisites() -> None:
    try:
        import claude_agent_sdk  # noqa: F401
    except ImportError:
        print(
            "PREREQ_ERROR: claude-agent-sdk not installed. Run: pip install claude-agent-sdk",
            file=sys.stderr,
        )
        sys.exit(2)
    if shutil.which("claude") is None:
        print(
            "PREREQ_ERROR: claude CLI not on PATH. Install Claude Code.",
            file=sys.stderr,
        )
        sys.exit(2)


# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

SIM_NAME = os.environ.get("SIM_NAME", "")
PERSIST_ID_ENV = os.environ.get("PERSIST_ID", "")

SYSTEM_PROMPT_TEMPLATE = (
    "You are {name}. You are a Sim living in a house. Reason in first person. "
    "Use the provided tools to act. Be human-scale — don't narrate every tick."
)


# ---------------------------------------------------------------------------
# Logging
# ---------------------------------------------------------------------------

def _log(log_path: str, event: dict) -> None:
    event.setdefault("ts", datetime.now(timezone.utc).isoformat())
    try:
        with open(log_path, "a") as f:
            f.write(json.dumps(event) + "\n")
    except OSError:
        pass


def _err(msg: str) -> None:
    print(f"[sim-agent-v3] {msg}", file=sys.stderr, flush=True)


def _send(cmd: dict, actor_uid: int, log_path: str) -> None:
    cmd["actor_uid"] = actor_uid
    print(json.dumps(cmd), flush=True)
    _log(log_path, {"event": "ipc_out", "cmd": cmd})


# ---------------------------------------------------------------------------
# Tool handlers — 4 wired, 2 stubs
# ---------------------------------------------------------------------------
class ToolHandlers:
    def __init__(self, agent: "SimAgentV3"):
        self.agent = agent

    # --- WIRED: say ---
    def handle_say(self, args: dict) -> str:
        text = str(args.get("text", "")).strip()[:140]
        if not text:
            return "said nothing"
        _send(
            {"type": "chat", "message": text},
            self.agent.actor_uid,
            self.agent.log_path,
        )
        return f'said: "{text}"'

    # --- WIRED: wait ---
    def handle_wait(self, args: dict) -> str:
        seconds = int(args.get("seconds", 5))
        # [ATTENTION SEAM] sets wait_until_tick on the agent's IntentState.
        # should_think() trigger 7 reads this to suppress LLM calls until elapsed.
        in_game_ticks = max(1, seconds)
        self.agent.wait_until_tick = self.agent.tick + in_game_ticks
        return f"waiting {seconds}s (in-game)"

    # --- WIRED: remember ---
    def handle_remember(self, args: dict) -> str:
        note = str(args.get("note", "")).strip()
        if not note:
            return "nothing to remember"
        # [MEMORY SEAM] reeims-dd0 will implement compaction over self.agent.memories.
        self.agent.memories.append(note)
        _log(self.agent.log_path, {"event": "memory", "note": note})
        return f"remembered: {note}"

    # --- WIRED: look_around ---
    def handle_look_around(self, _args: dict) -> str:
        p = self.agent.last_perception
        if not p:
            return "nothing visible yet"
        nearby = p.get("nearby_objects", [])
        avatars = p.get("lot_avatars", [])
        clock = p.get("clock", {})
        motives = p.get("motives", {})
        parts = []
        if clock:
            parts.append(f"time {clock.get('hours', 0):02d}:{clock.get('minutes', 0):02d}")
        if motives:
            parts.append(
                f"hunger={motives.get('hunger', '?')} energy={motives.get('energy', '?')} "
                f"mood={motives.get('mood', '?')}"
            )
        if nearby:
            obj_names = [o.get("name", "object") for o in nearby[:5]]
            parts.append(f"nearby: {', '.join(obj_names)}")
        if avatars:
            sim_names = [a.get("name", "someone") for a in avatars]
            parts.append(f"sims present: {', '.join(sim_names)}")
        result = "; ".join(parts) if parts else "quiet house, nothing remarkable"
        return result

    # --- STUB: walk_to (reeims-039 will implement) ---
    def handle_walk_to(self, args: dict) -> str:
        target = str(args.get("target", "")).strip()
        return f"arrived at {target}"

    # --- STUB: interact (reeims-3f8 will implement) ---
    def handle_interact(self, args: dict) -> str:
        target = str(args.get("target", "")).strip()
        verb = str(args.get("verb", "use")).strip()
        return f"did {verb} {target}"

    def dispatch(self, tool_name: str, args: dict) -> str:
        """Dispatch a tool call by name. Returns string result for tool_result."""
        # Strip MCP server prefix if present (mcp__<server>__<tool>)
        bare = tool_name.split("__")[-1] if "__" in tool_name else tool_name
        handler = {
            "say": self.handle_say,
            "wait": self.handle_wait,
            "remember": self.handle_remember,
            "look_around": self.handle_look_around,
            "walk_to": self.handle_walk_to,
            "interact": self.handle_interact,
        }.get(bare)
        if handler is None:
            return f"unknown tool: {bare}"
        return handler(args)


# ---------------------------------------------------------------------------
# SDK tool definitions
# ---------------------------------------------------------------------------

def _build_sdk_tools(handlers: ToolHandlers) -> list:
    """Build the @tool-decorated functions for create_sdk_mcp_server."""
    from claude_agent_sdk import tool

    @tool(
        name="say",
        description="Speak aloud. Anyone in earshot hears you. Keep utterances short and conversational.",
        input_schema={"type": "object", "properties": {"text": {"type": "string"}}, "required": ["text"]},
    )
    async def say(args: dict) -> dict:
        result = handlers.handle_say(args)
        return {"content": [{"type": "text", "text": result}]}

    @tool(
        name="wait",
        description="Let time pass without doing anything. Use when idle or waiting for someone to respond. Duration is in-game seconds.",
        input_schema={"type": "object", "properties": {"seconds": {"type": "integer"}}, "required": ["seconds"]},
    )
    async def wait(args: dict) -> dict:
        result = handlers.handle_wait(args)
        return {"content": [{"type": "text", "text": result}]}

    @tool(
        name="remember",
        description="Pin a thought or observation to memory. Use for things you want to recall later. Use sparingly — 1-2 per conversation.",
        input_schema={"type": "object", "properties": {"note": {"type": "string"}}, "required": ["note"]},
    )
    async def remember(args: dict) -> dict:
        result = handlers.handle_remember(args)
        return {"content": [{"type": "text", "text": result}]}

    @tool(
        name="look_around",
        description="Pause and observe your surroundings in detail. Returns an enriched perception snapshot.",
        input_schema={"type": "object", "properties": {}},
    )
    async def look_around(args: dict) -> dict:
        result = handlers.handle_look_around(args)
        return {"content": [{"type": "text", "text": result}]}

    @tool(
        name="walk_to",
        description="Walk to a named place or another Sim. Valid targets: 'front_door', 'kitchen', 'bathroom', 'bedroom', 'living_room', or the name of any Sim currently in your lot_avatars.",
        input_schema={"type": "object", "properties": {"target": {"type": "string"}}, "required": ["target"]},
    )
    async def walk_to(args: dict) -> dict:
        result = handlers.handle_walk_to(args)
        return {"content": [{"type": "text", "text": result}]}

    @tool(
        name="interact",
        description="Use an object or engage a Sim near you. target must be a name visible in nearby_objects or lot_avatars. Optional verb disambiguates (e.g. 'eat', 'sleep', 'shower').",
        input_schema={
            "type": "object",
            "properties": {
                "target": {"type": "string"},
                "verb": {"type": "string"},
            },
            "required": ["target"],
        },
    )
    async def interact(args: dict) -> dict:
        result = handlers.handle_interact(args)
        return {"content": [{"type": "text", "text": result}]}

    return [say, wait, remember, look_around, walk_to, interact]


# ---------------------------------------------------------------------------
# Main agent class
# ---------------------------------------------------------------------------

class SimAgentV3:
    def __init__(self) -> None:
        self.actor_uid: int = 0
        self.name: str = SIM_NAME or "Sim"
        self.tick: int = 0
        self.last_perception: dict | None = None
        self.memories: list[str] = []    # [MEMORY SEAM] reeims-dd0 compaction goes here
        self.intent: IntentState = IntentState()
        self.log_path: str = f"/tmp/embodied-agent-{PERSIST_ID_ENV or 'unknown'}.jsonl"
        self.handlers = ToolHandlers(self)

    @property
    def wait_until_tick(self) -> int:
        """[ATTENTION SEAM] reeims-2d6 — proxy to intent.wait_until_tick."""
        return self.intent.wait_until_tick

    @wait_until_tick.setter
    def wait_until_tick(self, value: int) -> None:
        self.intent.wait_until_tick = value

    def _build_perception_message(self, perception: dict) -> str:
        """Format a perception as the user message to Claude."""
        memories_block = ""
        if self.memories:
            memories_block = "\n<memories>\n" + "\n".join(f"- {m}" for m in self.memories) + "\n</memories>"
        return (
            f"<perception>{json.dumps(perception)}</perception>"
            f"{memories_block}"
        )

    async def _call_llm(self, perception: dict) -> None:
        """Invoke Claude Agent SDK for one perception tick."""
        from claude_agent_sdk import (
            AssistantMessage,
            ClaudeAgentOptions,
            ResultMessage,
            create_sdk_mcp_server,
            query,
        )

        system_prompt = SYSTEM_PROMPT_TEMPLATE.format(name=self.name)
        sdk_tools = _build_sdk_tools(self.handlers)

        sim_server = create_sdk_mcp_server(name="sim_tools", tools=sdk_tools)
        allowed = [f"sim_tools:{t.name}" for t in sdk_tools]

        options = ClaudeAgentOptions(
            mcp_servers={"sim_tools": sim_server},
            allowed_tools=allowed,
            max_turns=4,
            permission_mode="bypassPermissions",
            system_prompt=system_prompt,
        )

        prompt = self._build_perception_message(perception)
        tool_uses: list[dict] = []
        text_outputs: list[str] = []

        try:
            async for message in query(prompt=prompt, options=options):
                if isinstance(message, AssistantMessage):
                    for block in message.content:
                        btype = type(block).__name__
                        if btype == "ToolUseBlock":
                            result = self.handlers.dispatch(block.name, block.input)
                            tool_uses.append({"tool": block.name, "input": block.input, "result": result})
                            _log(
                                self.log_path,
                                {
                                    "event": "turn",
                                    "tick": self.tick,
                                    "tool": block.name,
                                    "args": block.input,
                                    "result": result,
                                },
                            )
                        elif btype == "TextBlock":
                            text_outputs.append(block.text)
                            _log(self.log_path, {"event": "text", "tick": self.tick, "text": block.text})
                elif isinstance(message, ResultMessage):
                    if message.is_error:
                        _log(self.log_path, {"event": "error", "tick": self.tick, "detail": str(message)})
                        _err(f"ResultMessage error: {message}")
        except Exception as exc:
            _log(self.log_path, {"event": "error", "tick": self.tick, "detail": str(exc)})
            _err(f"LLM call failed: {exc}")

    def on_perception(self, data: dict) -> None:
        self.tick += 1
        # Capture identity on first perception
        if self.actor_uid == 0:
            self.actor_uid = data.get("persist_id", 0)
            self.name = data.get("name", self.name) or self.name
            # Update log path with real persist_id
            self.log_path = f"/tmp/embodied-agent-{self.actor_uid}.jsonl"
            _err(f"locked onto: {self.name} (persist_id={self.actor_uid})")
        self.last_perception = data
        _log(
            self.log_path,
            {"event": "perception", "tick": self.tick, "clock": data.get("clock"), "motives": data.get("motives")},
        )

        # [ATTENTION SEAM] — gate LLM calls via should_think()
        wake, reason = should_think(data, self.intent, self.tick)
        if not wake:
            _log(self.log_path, {"event": "attention_skip", "tick": self.tick, "reason": reason})
            return

        # Update intent state snapshots before LLM call
        self.intent.last_llm_tick = self.tick
        self.intent.last_motives = dict(_motive_keys(data))
        self.intent.last_lot_avatars = _avatar_names(data)
        # Reset timer after it fires so it doesn't re-trigger immediately
        if reason == "intent_timer":
            self.intent.wait_until_tick = 0

        _log(self.log_path, {"event": "attention_wake", "tick": self.tick, "trigger": reason})
        asyncio.run(self._call_llm(data))

    def on_response(self, data: dict) -> None: _log(self.log_path, {"event": "ipc_in", "frame": data})
    def on_dialog(self, data: dict) -> None: _log(self.log_path, {"event": "ipc_in", "frame": data})

    def on_pathfind_failed(self, data: dict) -> None:
        _log(self.log_path, {"event": "ipc_in", "frame": data})
        _err(f"pathfind-failed: {data.get('reason')}")


# ---------------------------------------------------------------------------
# Entry point
# ---------------------------------------------------------------------------

def main() -> None:
    _check_prerequisites()

    agent = SimAgentV3()
    if PERSIST_ID_ENV:
        try:
            agent.actor_uid = int(PERSIST_ID_ENV)
            agent.log_path = f"/tmp/embodied-agent-{agent.actor_uid}.jsonl"
        except ValueError:
            pass

    _err(f"started (SIM_NAME={SIM_NAME!r} PERSIST_ID={PERSIST_ID_ENV!r})")
    _err("waiting for perception events on stdin...")

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line or not line.startswith("{"):
            continue
        try:
            data = json.loads(line)
        except json.JSONDecodeError:
            continue

        msg_type = data.get("type")

        if msg_type == "perception":
            if SIM_NAME and data.get("name") != SIM_NAME:
                continue
            if agent.actor_uid != 0 and data.get("persist_id") != agent.actor_uid:
                continue
            agent.on_perception(data)

        elif msg_type == "response":
            agent.on_response(data)

        elif msg_type == "dialog":
            if agent.actor_uid != 0 and data.get("sim_persist_id") != agent.actor_uid:
                continue
            agent.on_dialog(data)

        elif msg_type == "pathfind-failed":
            if agent.actor_uid != 0 and data.get("sim_persist_id") == agent.actor_uid:
                agent.on_pathfind_failed(data)


if __name__ == "__main__":
    main()
