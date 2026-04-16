"""
Tests for scripts/sim_agent_mechanism_probe.py

Runs the probe as a subprocess against the real subscription path (Claude Code
Agent SDK + local claude CLI OAuth credentials). Does NOT mock the mechanism.

Prerequisites that must be present for these tests to pass:
  - claude-agent-sdk installed (pip install claude-agent-sdk)
  - claude CLI on PATH (/home/baron/.local/bin/claude or equivalent)
  - Active Claude Code OAuth session (~/.claude/ credentials)

If prerequisites are absent the probe exits with code 2 and a specific
PREREQ_ERROR message — this test will FAIL LOUDLY, not skip silently.
"""

import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

PROBE = Path(__file__).parent.parent / "scripts" / "sim_agent_mechanism_probe.py"


def _prereq_errors() -> list[str]:
    """Return list of unmet prerequisite descriptions (empty = all met)."""
    errors: list[str] = []
    try:
        import claude_agent_sdk  # noqa: F401
    except ImportError:
        errors.append("claude-agent-sdk not installed — run: pip install claude-agent-sdk")
    if shutil.which("claude") is None:
        errors.append("claude CLI not on PATH — install Claude Code")
    return errors


# Hard-fail rather than skip if prerequisites are missing
_MISSING = _prereq_errors()
if _MISSING:
    pytest.fail(
        "Probe prerequisites missing:\n" + "\n".join(f"  • {m}" for m in _MISSING),
        pytrace=False,
    )


class TestSimAgentMechanismProbe:
    def test_probe_exits_zero(self) -> None:
        """Probe must return exit code 0."""
        result = subprocess.run(
            [sys.executable, str(PROBE)],
            capture_output=True,
            text=True,
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Probe exited with code {result.returncode}.\n"
            f"stderr:\n{result.stderr}\n"
            f"stdout:\n{result.stdout}"
        )

    def test_probe_emits_tool_use_json(self) -> None:
        """Stdout must contain at least one tool_use JSON line."""
        result = subprocess.run(
            [sys.executable, str(PROBE)],
            capture_output=True,
            text=True,
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Probe failed (rc={result.returncode}):\n{result.stderr}"
        )
        lines = [line.strip() for line in result.stdout.splitlines() if line.strip()]
        tool_use_events = []
        for line in lines:
            try:
                obj = json.loads(line)
                if obj.get("event") == "tool_use":
                    tool_use_events.append(obj)
            except json.JSONDecodeError:
                pass

        assert tool_use_events, (
            f"No tool_use event found in probe output.\nstdout:\n{result.stdout}"
        )
        # Must be a get_sim_state call
        sim_state_calls = [e for e in tool_use_events if "get_sim_state" in e.get("tool", "")]
        assert sim_state_calls, (
            f"No get_sim_state tool_use found. Events: {tool_use_events}"
        )

    def test_probe_emits_tool_result_json(self) -> None:
        """Stdout must contain at least one tool_result JSON line with valid JSON output."""
        result = subprocess.run(
            [sys.executable, str(PROBE)],
            capture_output=True,
            text=True,
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Probe failed (rc={result.returncode}):\n{result.stderr}"
        )
        lines = [line.strip() for line in result.stdout.splitlines() if line.strip()]
        tool_result_events = []
        for line in lines:
            try:
                obj = json.loads(line)
                if obj.get("event") == "tool_result":
                    tool_result_events.append(obj)
            except json.JSONDecodeError:
                pass

        assert tool_result_events, (
            f"No tool_result event found in probe output.\nstdout:\n{result.stdout}"
        )
        # The output field must itself be valid JSON (the sim state dict)
        first = tool_result_events[0]
        assert "output" in first, f"tool_result missing 'output' field: {first}"
        parsed = json.loads(first["output"])
        assert "sim_id" in parsed, f"tool_result output missing 'sim_id': {parsed}"
        assert "hunger" in parsed, f"tool_result output missing 'hunger': {parsed}"

    def test_probe_full_round_trip(self) -> None:
        """Full round-trip: tool_use -> tool_result -> done (one cycle)."""
        result = subprocess.run(
            [sys.executable, str(PROBE)],
            capture_output=True,
            text=True,
            timeout=120,
        )
        assert result.returncode == 0, (
            f"Probe failed (rc={result.returncode}):\n{result.stderr}"
        )
        events = []
        for line in result.stdout.splitlines():
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                pass

        event_types = [e.get("event") for e in events]
        assert "tool_use" in event_types, f"Missing tool_use in events: {event_types}"
        assert "tool_result" in event_types, f"Missing tool_result in events: {event_types}"
        assert "done" in event_types, f"Missing done in events: {event_types}"

        # tool_use must precede tool_result which must precede done
        idx_tool_use = next(i for i, e in enumerate(events) if e.get("event") == "tool_use" and "get_sim_state" in e.get("tool", ""))
        idx_tool_result = next(i for i, e in enumerate(events) if e.get("event") == "tool_result")
        idx_done = next(i for i, e in enumerate(events) if e.get("event") == "done")
        assert idx_tool_use < idx_tool_result < idx_done, (
            f"Event ordering wrong: tool_use={idx_tool_use} tool_result={idx_tool_result} done={idx_done}"
        )

    def test_no_anthropic_imports(self) -> None:
        """Probe must not import anthropic or reference ANTHROPIC_API_KEY."""
        probe_text = PROBE.read_text()
        assert "import anthropic" not in probe_text, (
            "Probe contains 'import anthropic' — violates subscription-only constraint"
        )
        assert "ANTHROPIC_API_KEY" not in probe_text, (
            "Probe references ANTHROPIC_API_KEY — violates subscription-only constraint"
        )
