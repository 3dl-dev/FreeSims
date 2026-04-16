#!/usr/bin/env python3
"""
sim-agent-v3.py — LLM-driven Sim agent using Anthropic tool-use (Haiku).

Reads perception events from the FreeSims sidecar on stdin (line-delimited
JSON) and writes IPC commands to stdout. One agent process per Sim.

Protocol is identical to sim-agent-v2.py (same stdin/stdout contract).

Environment variables
---------------------
  SIM_AGENT_NAME    — filter to this Sim name (match against perception.name)
  PERSIST_ID        — override persist_id detection (set by demo launcher)
  ANTHROPIC_API_KEY — required. If absent the script exits with a clear error.

Logging
-------
  /tmp/embodied-agent-<persist_id>.jsonl — one JSON object per event:
    {"ts":..., "event":"turn", "trigger":..., "tool":..., "args":{...}}
    {"ts":..., "event":"ipc_out", "cmd":{...}}
    {"ts":..., "event":"ipc_in", "frame":{...}}
    {"ts":..., "event":"text", "content":"..."}
    {"ts":..., "event":"error", "message":"..."}

Tools registered
----------------
  say(text)           — FULL: emit {type:'chat',...}
  wait(seconds)       — FULL: set _next_wake_tick, return confirmation
  remember(note)      — FULL: append to memories list
  look_around()       — FULL: emit query-sim-state, await response
  walk_to(target)     — STUB: emit goto with placeholder (10,10)
  interact(target,verb)— STUB: emit interact with placeholder object_id=0

NOT in this item: attention controller, full walk_to/interact, memory compaction.
"""

import json
import os
import sys
import time
import signal
import threading
from datetime import datetime, timezone
from typing import Optional

# ---------------------------------------------------------------------------
# SDK guard — fail early with a clear message
# ---------------------------------------------------------------------------

try:
    import anthropic
except ImportError:
    print(
        "ERROR: 'anthropic' SDK not installed. Run: pip install anthropic",
        file=sys.stderr,
        flush=True,
    )
    sys.exit(1)

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

SIM_NAME = os.environ.get("SIM_AGENT_NAME", "")
PERSIST_ID_OVERRIDE = os.environ.get("PERSIST_ID", "")
REAL_TIMEOUT = 150          # 2.5 real-time minutes (same as v2)
INGAME_MINUTES_TARGET = 10  # 10 in-game minutes

SYSTEM_PROMPT_TEMPLATE = (
    "You are {name}. You are a Sim living in a house. "
    "The messages below describe your body, senses, and surroundings. "
    "Think in first person — you are {name}, not an observer of {name}. "
    "Use the provided tools to act in the world. "
    "Be human-scale and spontaneous. Do not narrate every tick — only act "
    "when something feels meaningful or urgent."
)

# ---------------------------------------------------------------------------
# Tool definitions (schemas per docs/embodied-sim-agents.md §Tool surface)
# ---------------------------------------------------------------------------

TOOLS = [
    {
        "name": "walk_to",
        "description": (
            "Walk to a named place or another Sim. Valid targets: 'front_door', "
            "'kitchen', 'bathroom', 'bedroom', 'living_room', or the name of any "
            "Sim currently in your lot_avatars. Returns when you arrive or the path "
            "is blocked."
        ),
        "input_schema": {
            "type": "object",
            "properties": {"target": {"type": "string"}},
            "required": ["target"],
        },
    },
    {
        "name": "interact",
        "description": (
            "Use an object or engage a Sim near you. target must be a name visible "
            "in nearby_objects or lot_avatars. Optional verb disambiguates when an "
            "object offers multiple interactions (e.g. 'eat', 'sleep', 'shower'). "
            "If omitted, the primary affordance is chosen."
        ),
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
        "description": (
            "Speak aloud. Anyone in earshot hears you. Keep utterances short and "
            "conversational."
        ),
        "input_schema": {
            "type": "object",
            "properties": {"text": {"type": "string"}},
            "required": ["text"],
        },
    },
    {
        "name": "look_around",
        "description": (
            "Pause and observe your surroundings in detail. Returns an enriched "
            "perception snapshot."
        ),
        "input_schema": {"type": "object", "properties": {}},
    },
    {
        "name": "remember",
        "description": (
            "Pin a thought or observation to memory. Use for things you want to "
            "recall later (e.g. 'Daisy seemed upset after dinner'). Use sparingly "
            "— 1-2 per conversation."
        ),
        "input_schema": {
            "type": "object",
            "properties": {"note": {"type": "string"}},
            "required": ["note"],
        },
    },
    {
        "name": "wait",
        "description": (
            "Let time pass without doing anything. Use when idle or waiting for "
            "someone to respond. Duration is in-game seconds."
        ),
        "input_schema": {
            "type": "object",
            "properties": {"seconds": {"type": "integer"}},
            "required": ["seconds"],
        },
    },
]

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------


def now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def send_ipc(cmd: dict, log_fn=None):
    """Write a command dict to stdout as JSONL."""
    line = json.dumps(cmd)
    print(line, flush=True)
    if log_fn:
        log_fn({"event": "ipc_out", "cmd": cmd})


# ---------------------------------------------------------------------------
# SimAgentV3
# ---------------------------------------------------------------------------


class SimAgentV3:
    def __init__(self):
        # Identity
        self.persist_id: Optional[int] = None
        self.name: str = SIM_NAME or "Sim"

        # Conversation state (Anthropic message list)
        self.conversation: list = []

        # Memories (raw list; compaction is a separate item)
        self.memories: list[str] = []

        # Perception tracking
        self.last_perception: Optional[dict] = None
        self.tick: int = 0
        self.real_start = time.monotonic()
        self.ingame_elapsed_minutes: float = 0.0
        self.ingame_start_minutes: Optional[int] = None
        self.clock_hours: Optional[int] = None
        self.clock_minutes: Optional[int] = None

        # look_around response rendezvous
        self._look_around_req_id: Optional[str] = None
        self._look_around_response: Optional[dict] = None
        self._look_around_event = threading.Event()
        self._look_around_lock = threading.Lock()
        self._look_around_seq: int = 0

        # wait() next-wake tracking
        self._next_wake_tick: Optional[int] = None

        # Log file (opened after persist_id is known)
        self._log_fh = None

        # Anthropic client
        api_key = os.environ.get("ANTHROPIC_API_KEY", "")
        if not api_key:
            print(
                "ERROR: ANTHROPIC_API_KEY is not set. Cannot initialise Anthropic client.",
                file=sys.stderr,
                flush=True,
            )
            sys.exit(1)
        self._client = anthropic.Anthropic(api_key=api_key)

    # -----------------------------------------------------------------------
    # Logging
    # -----------------------------------------------------------------------

    def _open_log(self):
        if self._log_fh is None and self.persist_id is not None:
            path = f"/tmp/embodied-agent-{self.persist_id}.jsonl"
            self._log_fh = open(path, "a", buffering=1)  # line-buffered

    def _log(self, record: dict):
        """Append a record to the agent JSONL log."""
        if self._log_fh is None:
            return
        record["ts"] = now_iso()
        self._log_fh.write(json.dumps(record) + "\n")

    def stderr(self, msg: str):
        print(f"[agent:{self.name}] {msg}", file=sys.stderr, flush=True)

    # -----------------------------------------------------------------------
    # Clock / identity helpers
    # -----------------------------------------------------------------------

    def _update_clock(self, clock: dict):
        hours = clock.get("hours", 0)
        minutes = clock.get("minutes", 0)

        if self.ingame_start_minutes is None:
            self.ingame_start_minutes = hours * 60 + minutes

        current_total = hours * 60 + minutes
        if self.clock_hours is not None:
            prev_total = self.clock_hours * 60 + (self.clock_minutes or 0)
            if current_total < prev_total and prev_total - current_total > 120:
                delta = 1440 - prev_total + current_total
            else:
                delta = max(0, current_total - prev_total)
            self.ingame_elapsed_minutes += delta

        self.clock_hours = hours
        self.clock_minutes = minutes

    def _lock_identity(self, perception: dict):
        """Set persist_id + name on first perception, open log."""
        if self.persist_id is None:
            if PERSIST_ID_OVERRIDE:
                self.persist_id = int(PERSIST_ID_OVERRIDE)
            else:
                self.persist_id = perception["persist_id"]
            self.name = perception.get("name", self.name)
            self._open_log()
            self.stderr(
                f"locked onto: {self.name} (persist_id={self.persist_id})"
            )

    # -----------------------------------------------------------------------
    # Tool handlers
    # -----------------------------------------------------------------------

    def _handle_say(self, args: dict) -> str:
        text = str(args.get("text", "")).strip()[:140]
        cmd = {"type": "chat", "actor_uid": self.persist_id, "message": text}
        send_ipc(cmd, self._log)
        return f"said: {text}"

    def _handle_wait(self, args: dict) -> str:
        seconds = int(args.get("seconds", 10))
        # 1 in-game second ≈ 1 tick (approximately). Store desired tick offset.
        self._next_wake_tick = self.tick + max(1, seconds)
        return f"waiting {seconds} in-game seconds"

    def _handle_remember(self, args: dict) -> str:
        note = str(args.get("note", "")).strip()
        if note:
            self.memories.append(note)
        return f"noted: {note}"

    def _handle_look_around(self, _args: dict) -> str:
        """Emit query-sim-state, wait up to 5s for a response frame."""
        with self._look_around_lock:
            self._look_around_seq += 1
            req_id = f"look-{self.persist_id}-{self._look_around_seq}"
            self._look_around_req_id = req_id
            self._look_around_response = None
            self._look_around_event.clear()

        cmd = {
            "type": "query-sim-state",
            "actor_uid": self.persist_id,
            "request_id": req_id,
        }
        send_ipc(cmd, self._log)

        got = self._look_around_event.wait(timeout=5.0)
        if got and self._look_around_response is not None:
            return json.dumps(self._look_around_response)
        # Timeout: return last known perception as fallback
        if self.last_perception:
            return json.dumps(self.last_perception)
        return '{"error": "no perception available yet"}'

    def _handle_walk_to(self, args: dict) -> str:
        """STUB: emit goto with placeholder coords. Full impl: reeims-<walk_to>."""
        target = args.get("target", "unknown")
        cmd = {
            "type": "goto",
            "actor_uid": self.persist_id,
            "x": 10,
            "y": 10,
            "level": 1,
        }
        send_ipc(cmd, self._log)
        return "arrived"  # stub always reports success

    def _handle_interact(self, args: dict) -> str:
        """STUB: emit interact with placeholder object_id=0. Full impl: reeims-<interact>."""
        target = args.get("target", "unknown")
        verb = args.get("verb", "use")
        cmd = {
            "type": "interact",
            "actor_uid": self.persist_id,
            "interaction_id": 0,
            "target_id": 0,
            "param0": 0,
        }
        send_ipc(cmd, self._log)
        return f"did {verb} {target}"

    _HANDLERS = {
        "say": _handle_say,
        "wait": _handle_wait,
        "remember": _handle_remember,
        "look_around": _handle_look_around,
        "walk_to": _handle_walk_to,
        "interact": _handle_interact,
    }

    def _dispatch_tool(self, name: str, args: dict) -> str:
        handler = self._HANDLERS.get(name)
        if handler is None:
            return f"unknown tool: {name}"
        try:
            return handler(self, args)
        except Exception as exc:
            msg = f"tool error in {name}: {exc}"
            self.stderr(msg)
            self._log({"event": "error", "message": msg})
            return msg

    # -----------------------------------------------------------------------
    # LLM turn
    # -----------------------------------------------------------------------

    def _build_user_message(self, trigger: str, perception: dict) -> str:
        """Compose the structured user message for a new LLM turn."""
        parts = [f"<trigger>{trigger}</trigger>"]

        # Perception block: full JSON, but self-only
        parts.append(f"<perception>{json.dumps(perception)}</perception>")

        # Memories block
        if self.memories:
            mem_text = "\n".join(f"- {m}" for m in self.memories)
            parts.append(f"<memories>\n{mem_text}\n</memories>")

        return "\n".join(parts)

    def _call_llm(self, trigger: str, perception: dict):
        """Run one LLM turn: perception → tool calls → tool results → done."""
        system_prompt = SYSTEM_PROMPT_TEMPLATE.format(name=self.name)
        user_content = self._build_user_message(trigger, perception)

        # Append user message to conversation
        self.conversation.append({"role": "user", "content": user_content})

        # Agentic loop: tool_use → tool_result → next response
        while True:
            try:
                response = self._client.messages.create(
                    model="claude-haiku-4-5",
                    max_tokens=512,
                    system=system_prompt,
                    tools=TOOLS,
                    messages=self.conversation,
                )
            except Exception as exc:
                msg = f"Anthropic API error: {exc}"
                self.stderr(msg)
                self._log({"event": "error", "message": msg})
                # Remove the user message we just appended so state stays clean
                self.conversation.pop()
                return

            # Append assistant response to conversation
            assistant_message = {"role": "assistant", "content": response.content}
            self.conversation.append(assistant_message)

            # Collect tool_use blocks
            tool_uses = [b for b in response.content if b.type == "tool_use"]
            text_blocks = [b for b in response.content if b.type == "text"]

            # Log any text the LLM produced
            for tb in text_blocks:
                self.stderr(f"[llm text] {tb.text[:200]}")
                self._log({"event": "text", "content": tb.text})

            if not tool_uses:
                # No tool call: end of turn
                break

            # Process tool calls and collect results
            tool_results = []
            for tu in tool_uses:
                name = tu.name
                args = tu.input if isinstance(tu.input, dict) else {}

                self.stderr(f"[tool] {name}({args})")
                self._log({"event": "turn", "trigger": trigger, "tool": name, "args": args})

                result = self._dispatch_tool(name, args)

                tool_results.append({
                    "type": "tool_result",
                    "tool_use_id": tu.id,
                    "content": result,
                })

            # Append tool results as user message
            self.conversation.append({"role": "user", "content": tool_results})

            # If stop_reason is end_turn, don't loop again
            if response.stop_reason == "end_turn":
                break

            # If stop_reason is tool_use, loop to get next response
            if response.stop_reason != "tool_use":
                break

    # -----------------------------------------------------------------------
    # Incoming frame handlers
    # -----------------------------------------------------------------------

    def on_perception(self, data: dict):
        self._lock_identity(data)

        self.last_perception = data
        self.tick += 1

        clock = data.get("clock", {})
        self._update_clock(clock)

        motives = data.get("motives", {})
        self.stderr(
            f"[tick {self.tick}] {self.name}: "
            f"hunger={motives.get('hunger')} energy={motives.get('energy')} "
            f"elapsed={self.ingame_elapsed_minutes:.1f}in-game-min"
        )

        self._log({"event": "ipc_in", "frame": {"type": "perception", "tick": self.tick}})

        # Attention: call LLM on every perception (attention controller is a separate item)
        # Determine trigger reason (simple heuristic)
        hunger = motives.get("hunger", 100)
        energy = motives.get("energy", 100)
        if hunger < 30:
            trigger = "hunger_low"
        elif energy < 30:
            trigger = "energy_low"
        elif self.tick == 1:
            trigger = "first_perception"
        else:
            trigger = "periodic"

        self._call_llm(trigger, data)

    def on_response(self, data: dict):
        """Handle response frames from the sidecar (e.g. look_around results)."""
        req_id = data.get("request_id", "")
        self._log({"event": "ipc_in", "frame": {"type": "response", "request_id": req_id}})

        with self._look_around_lock:
            if req_id == self._look_around_req_id:
                self._look_around_response = data.get("payload", data)
                self._look_around_event.set()

    def on_dialog(self, data: dict):
        """Auto-dismiss dialogs — same as v2, no LLM needed for basic dismiss."""
        dialog_id = data.get("dialog_id")
        buttons = data.get("buttons", [])
        title = data.get("title", "")
        self.stderr(f"dialog: id={dialog_id} title='{title}' buttons={buttons}")
        self._log({"event": "ipc_in", "frame": {"type": "dialog", "dialog_id": dialog_id}})

        if dialog_id:
            btn = "Yes" if "Yes" in buttons else (buttons[0] if buttons else "")
            if btn:
                cmd = {"type": "dialog-response", "dialog_id": dialog_id, "button": btn}
                send_ipc(cmd, self._log)

    # -----------------------------------------------------------------------
    # Exit logic
    # -----------------------------------------------------------------------

    def should_exit(self) -> bool:
        elapsed_real = time.monotonic() - self.real_start
        if elapsed_real >= REAL_TIMEOUT:
            self.stderr(f"real-time timeout ({REAL_TIMEOUT}s)")
            return True
        if self.ingame_elapsed_minutes >= INGAME_MINUTES_TARGET:
            self.stderr(
                f"in-game target reached ({self.ingame_elapsed_minutes:.1f}m "
                f">= {INGAME_MINUTES_TARGET}m)"
            )
            return True
        return False

    def summary(self):
        self.stderr("=== SUMMARY ===")
        self.stderr(f"  Sim: {self.name} (persist_id={self.persist_id})")
        self.stderr(f"  In-game elapsed: {self.ingame_elapsed_minutes:.1f} minutes")
        self.stderr(f"  Real elapsed: {time.monotonic() - self.real_start:.1f}s")
        self.stderr(f"  Ticks: {self.tick}")
        self.stderr(f"  Memories: {len(self.memories)}")
        self.stderr(f"  Conversation turns: {len(self.conversation)}")

    def close(self):
        if self._log_fh:
            self._log_fh.close()
            self._log_fh = None


# ---------------------------------------------------------------------------
# Main loop
# ---------------------------------------------------------------------------


def main():
    agent = SimAgentV3()

    def _sigterm_handler(signum, frame):
        agent.summary()
        agent.close()
        sys.exit(0)

    signal.signal(signal.SIGTERM, _sigterm_handler)

    agent.stderr(f"started (SIM_NAME={SIM_NAME!r})")
    agent.stderr("waiting for perception events on stdin...")

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
            # Filter to our Sim
            if SIM_NAME and data.get("name") != SIM_NAME:
                continue
            if agent.persist_id is not None and data.get("persist_id") != agent.persist_id:
                continue
            agent.on_perception(data)

        elif msg_type == "response":
            agent.on_response(data)

        elif msg_type == "dialog":
            if agent.persist_id is not None and data.get("sim_persist_id") != agent.persist_id:
                continue
            agent.on_dialog(data)

        elif msg_type == "pathfind-failed":
            if agent.persist_id is not None and data.get("sim_persist_id") == agent.persist_id:
                agent.stderr(f"pathfind-failed: reason={data.get('reason')}")
                agent._log({"event": "ipc_in", "frame": {"type": "pathfind-failed"}})

        if agent.should_exit():
            break

    agent.summary()
    agent.close()


if __name__ == "__main__":
    main()
