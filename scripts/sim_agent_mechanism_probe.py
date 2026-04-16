#!/usr/bin/env python3
"""
Minimal probe for the Claude Code Agent SDK mechanism.

Invokes claude-agent-sdk against a stub tool registry and round-trips
ONE tool call: get_sim_state -> tool_result. Exits 0 on success.

Usage:
    python3 scripts/sim_agent_mechanism_probe.py

Output (on stdout, JSON lines):
    {"event": "tool_use", "tool": "<name>", "input": {...}}
    {"event": "tool_result", "tool": "<name>", "output": "<text>"}
    {"event": "done", "text": "<final assistant text>"}
"""

import asyncio
import json
import sys


def _check_prerequisites() -> None:
    """Fail loudly if prerequisites are missing."""
    try:
        import claude_agent_sdk  # noqa: F401
    except ImportError:
        print(
            "PREREQ_ERROR: claude-agent-sdk not installed. "
            "Run: pip install claude-agent-sdk",
            file=sys.stderr,
        )
        sys.exit(2)

    import shutil

    if shutil.which("claude") is None:
        print(
            "PREREQ_ERROR: claude CLI not on PATH. "
            "Install Claude Code from https://claude.ai/code",
            file=sys.stderr,
        )
        sys.exit(2)


async def _run_probe() -> None:
    from claude_agent_sdk import (
        ClaudeAgentOptions,
        AssistantMessage,
        ResultMessage,
        create_sdk_mcp_server,
        query,
        tool,
    )

    # --- Stub tool registry (mirrors the sim-agent-v3 surface) ---
    tool_use_log: list[dict] = []
    tool_result_log: list[dict] = []

    @tool(
        name="get_sim_state",
        description="Return the current Sim's state as JSON.",
        input_schema={"sim_id": str},
    )
    async def get_sim_state(args: dict) -> dict:
        sim_id = args.get("sim_id", "daisy")
        result_text = json.dumps(
            {
                "sim_id": sim_id,
                "mood": "neutral",
                "hunger": 40,
                "energy": 80,
                "position": {"x": 5, "y": 3},
            }
        )
        tool_result_log.append({"tool": "get_sim_state", "output": result_text})
        print(
            json.dumps({"event": "tool_result", "tool": "get_sim_state", "output": result_text}),
            flush=True,
        )
        return {"content": [{"type": "text", "text": result_text}]}

    sim_tools_server = create_sdk_mcp_server(
        name="sim_stub",
        tools=[get_sim_state],
    )

    options = ClaudeAgentOptions(
        mcp_servers={"sim_stub": sim_tools_server},
        allowed_tools=["sim_stub:get_sim_state"],
        max_turns=4,
        permission_mode="bypassPermissions",
        system_prompt=(
            "You are a Sim agent. When asked about a Sim's state, "
            "call get_sim_state once, then summarise the result in one sentence. "
            "Do not call any other tools."
        ),
    )

    prompt = "Call get_sim_state for sim_id='daisy', then tell me her hunger level."

    async for message in query(prompt=prompt, options=options):
        if isinstance(message, AssistantMessage):
            for block in message.content:
                block_type = type(block).__name__
                if block_type == "ToolUseBlock":
                    entry = {"event": "tool_use", "tool": block.name, "input": block.input}
                    tool_use_log.append({"tool": block.name, "input": block.input})
                    print(json.dumps(entry), flush=True)
                elif block_type == "TextBlock":
                    print(json.dumps({"event": "done", "text": block.text}), flush=True)
        elif isinstance(message, ResultMessage):
            if message.is_error:
                print(
                    f"PROBE_ERROR: ResultMessage is_error=True: {message}",
                    file=sys.stderr,
                )
                sys.exit(1)

    # Validate round-trip
    if not tool_use_log:
        print("PROBE_ERROR: no tool_use observed", file=sys.stderr)
        sys.exit(1)
    if not tool_result_log:
        print("PROBE_ERROR: no tool_result observed", file=sys.stderr)
        sys.exit(1)

    # MCP tool names are prefixed with "mcp__<server>__" by the SDK
    sim_state_calls = [
        e for e in tool_use_log if "get_sim_state" in e["tool"]
    ]
    if not sim_state_calls:
        print(
            f"PROBE_ERROR: expected get_sim_state tool call, got {[e['tool'] for e in tool_use_log]}",
            file=sys.stderr,
        )
        sys.exit(1)


def main() -> None:
    _check_prerequisites()
    asyncio.run(_run_probe())


if __name__ == "__main__":
    main()
