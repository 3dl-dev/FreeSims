#!/usr/bin/env python3
"""
sim-agent.py — Claude-powered Sim agent.

Reads perception events from the FreeSims sidecar (stdin pipe), sends them to
Claude for reasoning, and writes interaction commands back (stdout pipe).

Usage:
    # Start game + sidecar, then pipe through the agent:
    mkfifo /tmp/agent-pipe
    freesims-sidecar --sock /tmp/freesims-ipc.sock < /tmp/agent-pipe | \
        python3 scripts/sim-agent.py > /tmp/agent-pipe

    # Or use the launch script:
    scripts/demo-agent.sh
"""

import json
import sys
import os
import time
import anthropic

MODEL = os.environ.get("SIM_AGENT_MODEL", "claude-haiku-4-5-20251001")
SIM_NAME = os.environ.get("SIM_AGENT_NAME", "")  # empty = control first Sim seen

client = anthropic.Anthropic()

SYSTEM_PROMPT = """You are controlling a Sim in a life simulation game. You receive perception events
describing your Sim's current state and nearby objects. You must decide what your Sim should do.

PERCEPTION FORMAT:
You receive JSON with: name, motives (hunger/comfort/energy/hygiene/bladder/room/social/fun/mood,
range -100 to +100, higher is better), position, nearby_objects (each with interactions you can perform).

RESPONSE FORMAT:
Respond with a single JSON command on one line. Available commands:

1. Interact with an object:
   {"type":"interact","actor_uid":PERSIST_ID,"interaction_id":ID,"target_id":OBJECT_ID,"param0":0}

2. Say something:
   {"type":"chat","actor_uid":PERSIST_ID,"message":"Hello!"}

3. Do nothing (wait for next perception):
   {"type":"wait"}

RULES:
- Prioritize survival: address the LOWEST motive first (hunger, bladder, energy, hygiene)
- If all motives are healthy (>25), pursue fun or social interactions
- Pick interactions from the nearby_objects list — use the exact object_id and interaction id
- If no suitable objects are nearby, say something or wait
- Be concise in your reasoning — one sentence max before the JSON command
- ONLY output the JSON command line, nothing else"""

conversation_history = []
my_persist_id = None
tick_count = 0


def log(msg):
    print(f"[agent] {msg}", file=sys.stderr)


def ask_claude(perception):
    global conversation_history

    user_msg = json.dumps(perception)

    # Keep conversation short — last 6 exchanges max
    if len(conversation_history) > 12:
        conversation_history = conversation_history[-12:]

    conversation_history.append({"role": "user", "content": user_msg})

    try:
        response = client.messages.create(
            model=MODEL,
            max_tokens=256,
            system=SYSTEM_PROMPT,
            messages=conversation_history,
        )
        text = response.content[0].text.strip()
        conversation_history.append({"role": "assistant", "content": text})
        return text
    except Exception as e:
        log(f"Claude API error: {e}")
        return '{"type":"wait"}'


def extract_command(response_text):
    """Extract JSON command from Claude's response (may have reasoning text before it)."""
    for line in response_text.split("\n"):
        line = line.strip()
        if line.startswith("{"):
            try:
                return json.loads(line)
            except json.JSONDecodeError:
                pass
    return {"type": "wait"}


def main():
    global my_persist_id, tick_count

    log(f"Sim agent started (model={MODEL})")
    log("Waiting for perception events on stdin...")

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        # Skip non-JSON lines (tick acks are printed as JSON too)
        if not line.startswith("{"):
            continue

        try:
            data = json.loads(line)
        except json.JSONDecodeError:
            continue

        # Only process perception events
        if data.get("type") != "perception":
            continue

        # Filter to our Sim if specified
        if SIM_NAME and data.get("name") != SIM_NAME:
            continue

        # Lock onto first Sim we see
        if my_persist_id is None:
            my_persist_id = data["persist_id"]
            log(f"Controlling: {data['name']} (persist_id={my_persist_id})")
        elif data.get("persist_id") != my_persist_id:
            continue

        tick_count += 1
        motives = data.get("motives", {})
        nearby = data.get("nearby_objects", [])
        log(f"[tick {tick_count}] {data['name']}: hunger={motives.get('hunger')} energy={motives.get('energy')} nearby={len(nearby)} objects")

        # Ask Claude what to do
        response_text = ask_claude(data)
        cmd = extract_command(response_text)

        if cmd.get("type") == "wait":
            log(f"  -> waiting")
            continue

        # Inject our persist_id
        if "actor_uid" in cmd:
            cmd["actor_uid"] = my_persist_id

        # Send command to sidecar (stdout)
        cmd_json = json.dumps(cmd)
        print(cmd_json, flush=True)
        log(f"  -> {cmd.get('type')}: {cmd_json[:80]}")


if __name__ == "__main__":
    main()
