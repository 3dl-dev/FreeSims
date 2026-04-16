#!/usr/bin/env python3
"""
sim-agent-v4 — embodied Sim consciousness, driven by campfire conventions.

The agent does not know it is "an LLM playing a Sim." Its universe IS the
campfire: it joins a single channel (freesims.lot), reads perception messages
for its own sim, calls the `help` convention to discover what it can do, and
issues actions via convention operations (walk-to, speak, interact-with).

The LLM sees the convention schemas as the description of the universe's
affordances. It outputs a single JSON intent. The script invokes that intent
as a convention operation and reads the fulfillment. Loop.

No MCP. No in-process tool server. The channel is the wire. The conventions
are the API.

Environment variables:
  SIM_NAME         — name of the Sim this agent embodies (e.g. "Daisy")
  SIM_PERSIST_ID   — persist_id of the Sim (matches perception.persist_id)
  CF_HOME          — campfire config dir (defaults to ~/.cf)
  FREESIMS_CF_LOT  — lot campfire ID emitted by the sidecar
"""

from __future__ import annotations

import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

SIM_NAME = os.environ.get("SIM_NAME", "").strip()
SIM_PERSIST_ID = int(os.environ.get("SIM_PERSIST_ID", "0") or 0)
CF_HOME = os.environ.get("CF_HOME", str(Path.home() / ".cf"))
LOT_ID = os.environ.get("FREESIMS_CF_LOT", "").strip()

LOG_PATH = f"/tmp/embodied-agent-{SIM_PERSIST_ID or SIM_NAME or os.getpid()}.jsonl"


def _log(event: dict) -> None:
    event.setdefault("ts", datetime.now(timezone.utc).isoformat())
    with open(LOG_PATH, "a") as f:
        f.write(json.dumps(event) + "\n")


def _err(msg: str) -> None:
    print(f"[sim-agent-v4] {msg}", file=sys.stderr, flush=True)


def cf(*args: str, input_bytes: bytes | None = None, timeout: float = 30.0) -> dict:
    """Invoke `cf` CLI and parse the JSON result. Raises on non-zero exit."""
    cmd = ["cf", "--cf-home", CF_HOME, "--json", *args]
    res = subprocess.run(
        cmd, capture_output=True, input=input_bytes, timeout=timeout
    )
    if res.returncode != 0:
        raise RuntimeError(
            f"cf {' '.join(args)} failed (exit {res.returncode}):\n"
            f"stderr: {res.stderr.decode(errors='replace')}\n"
            f"stdout: {res.stdout.decode(errors='replace')}"
        )
    try:
        return json.loads(res.stdout or b"{}")
    except json.JSONDecodeError:
        # Some commands emit a stream of JSON objects on stdout
        return {"_raw": res.stdout.decode(errors="replace")}


def cf_read_messages(lot_id: str, after_ts: int | None = None) -> list[dict]:
    """Read raw messages from the lot. Returns a list of message dicts."""
    args = [lot_id]
    if after_ts is not None:
        args.extend(["--after", str(after_ts)])
    result = cf(*args)
    # cf <id> output shape varies — be tolerant
    if isinstance(result, dict):
        return result.get("messages") or []
    if isinstance(result, list):
        return result
    return []


def load_conventions(lot_id: str) -> list[dict]:
    """Read convention declarations published on the lot campfire.
    Declarations are messages tagged 'convention:operation'."""
    msgs = cf_read_messages(lot_id)
    decls = []
    for m in msgs:
        tags = m.get("tags") or []
        if "convention:operation" not in tags:
            continue
        payload = m.get("payload")
        if isinstance(payload, str):
            try:
                payload = json.loads(payload)
            except Exception:
                continue
        if isinstance(payload, dict) and payload.get("operation"):
            decls.append(payload)
    return decls


def render_universe_prompt(decls: list[dict], self_name: str, self_id: int) -> str:
    """Render the convention manifest as the Sim's universe description."""
    usable_ops = [d for d in decls if d.get("operation") in {"walk-to", "speak", "interact-with"}]
    lines = [
        f"You are {self_name}.",
        f"Your persist_id is {self_id}.",
        "",
        "Your universe has the following affordances. Each is a convention",
        "operation you can invoke. The name of each lists required and optional",
        "arguments. Results come back as fulfillments you will see in your next",
        "tick. Do not describe what you are going to do — do it.",
        "",
    ]
    for d in usable_ops:
        op = d["operation"]
        desc = d.get("description", "").strip()
        args = d.get("args") or []
        arg_strs = []
        for a in args:
            name = a.get("name")
            if name == "sim_id":
                continue  # agent fills this automatically
            req = "required" if a.get("required") else "optional"
            arg_strs.append(f"{name} ({a.get('type')}, {req}): {a.get('description','')}")
        lines.append(f"• {op}: {desc}")
        for a in arg_strs:
            lines.append(f"    - {a}")
        lines.append("")
    lines.extend([
        "On each perception tick you will receive a JSON description of your",
        "current situation: your position, your motives (hunger, energy, etc.),",
        "nearby objects (each with an `interactions` list), other sims in the",
        "lot, and recent events.",
        "",
        "Respond with exactly one line of JSON with the shape:",
        '  {"op": "<operation-name>", "args": {...}}',
        "or, if you choose inaction for this tick:",
        '  {"op": "wait"}',
        "",
        "Do not output prose. Only the JSON.",
    ])
    return "\n".join(lines)


def wait_for_perception(lot_id: str, self_id: int, since_ts: int, poll_s: float = 1.0, timeout_s: float = 60.0) -> tuple[dict | None, int]:
    """Poll the lot for the next perception message tagged sim:<self_id>.
    Returns (perception_payload_or_None, new_cursor_ts)."""
    sim_tag = f"sim:{self_id}"
    deadline = time.time() + timeout_s
    cursor = since_ts
    while time.time() < deadline:
        msgs = cf_read_messages(lot_id, after_ts=cursor)
        for m in msgs:
            ts = int(m.get("timestamp") or 0)
            if ts > cursor:
                cursor = ts
            tags = m.get("tags") or []
            if "freesims:perception" in tags and sim_tag in tags:
                payload = m.get("payload")
                if isinstance(payload, str):
                    try:
                        payload = json.loads(payload)
                    except Exception:
                        continue
                return payload, cursor
        time.sleep(poll_s)
    return None, cursor


def invoke_convention(lot_id: str, op: str, args: dict) -> dict:
    """Invoke a convention operation. Returns the fulfillment payload."""
    cli_args = [lot_id, op]
    for k, v in args.items():
        cli_args.extend([f"--{k}", str(v)])
    return cf(*cli_args)


def call_llm(system_prompt: str, perception: dict) -> dict:
    """Call the subscription-backed Claude via claude-agent-sdk and return the parsed
    JSON intent the model produced."""
    # Lazy import so the agent can at least start without the SDK
    from claude_agent_sdk import ClaudeAgentOptions, query
    import asyncio

    user_msg = "Your current perception:\n" + json.dumps(perception, indent=2) + "\n\nRespond with one JSON intent line."

    async def run() -> str:
        opts = ClaudeAgentOptions(
            system_prompt=system_prompt,
            max_turns=2,
            permission_mode="bypassPermissions",
            allowed_tools=[],  # the universe is conventions, not tools
        )
        text_parts: list[str] = []
        async for msg in query(prompt=user_msg, options=opts):
            klass = type(msg).__name__
            if klass == "AssistantMessage":
                for block in getattr(msg, "content", []) or []:
                    if type(block).__name__ == "TextBlock":
                        text_parts.append(block.text)
        return "".join(text_parts)

    raw = asyncio.run(run())
    raw = raw.strip()
    # Extract the first {...} JSON object from the output (tolerate stray text).
    start = raw.find("{")
    end = raw.rfind("}")
    if start < 0 or end < start:
        return {"op": "wait", "_parse_error": raw[:200]}
    try:
        return json.loads(raw[start : end + 1])
    except Exception as e:
        return {"op": "wait", "_parse_error": f"{e}: {raw[:200]}"}


def main() -> int:
    if not LOT_ID:
        _err("FREESIMS_CF_LOT is required")
        return 2
    if not SIM_NAME:
        _err("SIM_NAME is required")
        return 2
    if not SIM_PERSIST_ID:
        _err("SIM_PERSIST_ID is required")
        return 2

    _err(f"started (sim='{SIM_NAME}' persist_id={SIM_PERSIST_ID} lot={LOT_ID[:12]}…)")
    _log({"event": "start", "sim": SIM_NAME, "persist_id": SIM_PERSIST_ID, "lot": LOT_ID})

    try:
        decls = load_conventions(LOT_ID)
        _err(f"loaded {len(decls)} convention declarations")
        _log({"event": "universe", "conventions": [d.get("operation") for d in decls]})
    except Exception as e:
        _err(f"failed to load conventions: {e}")
        return 3

    system_prompt = render_universe_prompt(decls, SIM_NAME, SIM_PERSIST_ID)
    _log({"event": "system_prompt", "text": system_prompt})

    cursor = int(time.time() * 1000)  # skip history — only act on new perceptions
    while True:
        perception, cursor = wait_for_perception(LOT_ID, SIM_PERSIST_ID, cursor)
        if perception is None:
            continue
        _log({"event": "perception", "p": perception})

        intent = call_llm(system_prompt, perception)
        _log({"event": "intent", "intent": intent})

        op = intent.get("op")
        if not op or op == "wait":
            continue
        args = dict(intent.get("args") or {})
        args.setdefault("sim_id", SIM_PERSIST_ID)

        try:
            fulfillment = invoke_convention(LOT_ID, op, args)
            _log({"event": "fulfillment", "op": op, "result": fulfillment})
        except Exception as e:
            _log({"event": "error", "op": op, "error": str(e)})
            _err(f"invoke {op} failed: {e}")


if __name__ == "__main__":
    sys.exit(main() or 0)
