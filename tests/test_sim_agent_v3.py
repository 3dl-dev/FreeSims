"""
tests/test_sim_agent_v3.py — Unit + integration tests for sim-agent-v3.py.

Unit tests:
  - Mock Anthropic client returns a pre-canned tool_use response.
  - Verify tool dispatch path: tool_use → handler called → tool_result fed back.

Integration tests:
  - Feed 5 perception lines via pipe.
  - Verify ≥1 tool call on stdout, log file exists, valid JSON on stdout.
  - Skipped if ANTHROPIC_API_KEY is absent (with specific actionable reason).
"""

import json
import os
import subprocess
import sys
import tempfile
import threading
import time
from io import StringIO
from pathlib import Path
from types import SimpleNamespace
from typing import Any
from unittest.mock import MagicMock, patch, call

import importlib.util

import pytest

# ---------------------------------------------------------------------------
# Path setup — load sim-agent-v3.py (hyphenated name, not importable directly)
# ---------------------------------------------------------------------------

SCRIPTS_DIR = Path(__file__).parent.parent / "scripts"
_AGENT_MODULE_PATH = SCRIPTS_DIR / "sim-agent-v3.py"

# Load the module once at collection time so unit tests can reference SimAgentV3
os.environ.setdefault("ANTHROPIC_API_KEY", "test-key-unit")
_spec = importlib.util.spec_from_file_location("sim_agent_v3", _AGENT_MODULE_PATH)
_sim_agent_v3_module = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_sim_agent_v3_module)
SimAgentV3Class = _sim_agent_v3_module.SimAgentV3

# ---------------------------------------------------------------------------
# Fixtures / helpers
# ---------------------------------------------------------------------------

SAMPLE_PERCEPTION = {
    "type": "perception",
    "persist_id": 42,
    "name": "Daisy",
    "funds": 1000,
    "clock": {"hours": 8, "minutes": 0, "time_of_day": 1},
    "motives": {
        "hunger": 75,
        "energy": 60,
        "comfort": 50,
        "hygiene": 80,
        "bladder": 90,
        "social": 40,
        "fun": 55,
        "mood": 65,
    },
    "nearby_objects": [
        {"object_id": 101, "name": "Refrigerator", "interactions": [{"id": 1, "name": "eat"}]},
    ],
    "lot_avatars": [],
    "action_queue": [],
}


def _make_text_block(text: str):
    """Minimal stand-in for anthropic.types.TextBlock."""
    b = SimpleNamespace()
    b.type = "text"
    b.text = text
    return b


def _make_tool_use_block(tool_id: str, name: str, args: dict):
    """Minimal stand-in for anthropic.types.ToolUseBlock."""
    b = SimpleNamespace()
    b.type = "tool_use"
    b.id = tool_id
    b.name = name
    b.input = args
    return b


def _make_api_response(content_blocks, stop_reason="end_turn"):
    """Minimal stand-in for the anthropic messages.create response."""
    r = SimpleNamespace()
    r.content = content_blocks
    r.stop_reason = stop_reason
    return r


# ---------------------------------------------------------------------------
# Unit tests
# ---------------------------------------------------------------------------


class TestSimAgentV3Unit:
    """Unit tests with a mocked Anthropic client."""

    def _make_agent(self, persist_id=42, name="Daisy"):
        """Create a SimAgentV3 with the Anthropic client replaced by a mock."""
        agent = SimAgentV3Class.__new__(SimAgentV3Class)
        # Manual __init__ to avoid API key check + real client creation
        agent.persist_id = persist_id
        agent.name = name
        agent.conversation = []
        agent.memories = []
        agent.last_perception = None
        agent.tick = 0
        agent.real_start = time.monotonic()
        agent.ingame_elapsed_minutes = 0.0
        agent.ingame_start_minutes = None
        agent.clock_hours = None
        agent.clock_minutes = None
        agent._look_around_req_id = None
        agent._look_around_response = None
        agent._look_around_event = threading.Event()
        agent._look_around_lock = threading.Lock()
        agent._look_around_seq = 0
        agent._next_wake_tick = None
        agent._log_fh = None
        agent._client = MagicMock()
        return agent

    # ------------------------------------------------------------------
    # Tool handler unit tests
    # ------------------------------------------------------------------

    def test_say_emits_chat_to_stdout(self, capsys):
        agent = self._make_agent()
        result = agent._handle_say({"text": "Hello world"})
        captured = capsys.readouterr()
        out_lines = [l for l in captured.out.strip().splitlines() if l]
        assert len(out_lines) == 1
        cmd = json.loads(out_lines[0])
        assert cmd["type"] == "chat"
        assert cmd["actor_uid"] == 42
        assert cmd["message"] == "Hello world"
        assert "said:" in result

    def test_say_truncates_at_140_chars(self, capsys):
        agent = self._make_agent()
        long_text = "x" * 200
        agent._handle_say({"text": long_text})
        captured = capsys.readouterr()
        cmd = json.loads(captured.out.strip())
        assert len(cmd["message"]) == 140

    def test_wait_sets_next_wake_tick(self):
        agent = self._make_agent()
        agent.tick = 10
        result = agent._handle_wait({"seconds": 30})
        assert agent._next_wake_tick == 40  # tick + seconds
        assert "30" in result

    def test_remember_appends_to_memories(self):
        agent = self._make_agent()
        assert agent.memories == []
        result = agent._handle_remember({"note": "Daisy seemed upset"})
        assert agent.memories == ["Daisy seemed upset"]
        assert "Daisy seemed upset" in result

    def test_walk_to_stub_emits_goto(self, capsys):
        agent = self._make_agent()
        result = agent._handle_walk_to({"target": "kitchen"})
        captured = capsys.readouterr()
        cmd = json.loads(captured.out.strip())
        assert cmd["type"] == "goto"
        assert cmd["actor_uid"] == 42
        assert cmd["x"] == 10
        assert cmd["y"] == 10
        assert result == "arrived"

    def test_interact_stub_emits_interact(self, capsys):
        agent = self._make_agent()
        result = agent._handle_interact({"target": "refrigerator", "verb": "eat"})
        captured = capsys.readouterr()
        cmd = json.loads(captured.out.strip())
        assert cmd["type"] == "interact"
        assert cmd["actor_uid"] == 42
        assert cmd["target_id"] == 0  # stub placeholder
        assert "eat refrigerator" in result

    def test_look_around_emits_query_sim_state(self, capsys):
        """look_around emits query-sim-state and returns last_perception on timeout."""
        agent = self._make_agent()
        agent.last_perception = SAMPLE_PERCEPTION.copy()
        # Don't trigger the response event — let it timeout quickly
        result = agent._handle_look_around({})
        captured = capsys.readouterr()
        cmd = json.loads(captured.out.strip())
        assert cmd["type"] == "query-sim-state"
        assert cmd["actor_uid"] == 42
        # Should fall back to last_perception JSON string
        payload = json.loads(result)
        assert payload["persist_id"] == 42

    # ------------------------------------------------------------------
    # Tool dispatch unit test (the main loop behaviour)
    # ------------------------------------------------------------------

    def test_tool_dispatch_loop_say(self, capsys):
        """
        Core unit test: mock client returns tool_use(say), verify:
          1. handler dispatched → chat on stdout
          2. tool_result fed back as next user message in conversation
          3. second API call sees the tool_result
        """
        agent = self._make_agent()

        # First call: stop_reason=tool_use, content=[tool_use(say)]
        say_block = _make_tool_use_block("tu-1", "say", {"text": "Hi there"})
        first_response = _make_api_response([say_block], stop_reason="tool_use")

        # Second call: stop_reason=end_turn, no tools
        text_block = _make_text_block("Alright, done.")
        second_response = _make_api_response([text_block], stop_reason="end_turn")

        agent._client.messages.create.side_effect = [first_response, second_response]

        agent._call_llm("first_perception", SAMPLE_PERCEPTION)

        # stdout should contain the chat command
        captured = capsys.readouterr()
        out_lines = [l for l in captured.out.strip().splitlines() if l]
        assert len(out_lines) >= 1
        cmd = json.loads(out_lines[0])
        assert cmd["type"] == "chat"
        assert cmd["message"] == "Hi there"

        # conversation should have: [user, assistant(tool_use), user(tool_result), assistant(text)]
        assert len(agent.conversation) == 4
        # Third message should be the tool_result
        tool_result_msg = agent.conversation[2]
        assert tool_result_msg["role"] == "user"
        results = tool_result_msg["content"]
        assert isinstance(results, list)
        assert results[0]["type"] == "tool_result"
        assert results[0]["tool_use_id"] == "tu-1"
        assert "said:" in results[0]["content"]

        # Verify the client was called twice
        assert agent._client.messages.create.call_count == 2

    def test_tool_dispatch_remember(self):
        """remember() tool appended to memories and tool_result returned."""
        agent = self._make_agent()

        remember_block = _make_tool_use_block("tu-2", "remember", {"note": "test note"})
        first_response = _make_api_response([remember_block], stop_reason="tool_use")
        second_response = _make_api_response([], stop_reason="end_turn")
        agent._client.messages.create.side_effect = [first_response, second_response]

        agent._call_llm("periodic", SAMPLE_PERCEPTION)

        assert "test note" in agent.memories
        tool_result_msg = agent.conversation[2]
        assert "test note" in tool_result_msg["content"][0]["content"]

    def test_unknown_tool_returns_error_string(self):
        """Unknown tool name returns an error string, does not raise."""
        agent = self._make_agent()
        result = agent._dispatch_tool("nonexistent_tool", {})
        assert "unknown tool" in result

    def test_api_error_leaves_conversation_clean(self):
        """If the Anthropic API raises, the user message is popped and state is consistent."""
        agent = self._make_agent()
        initial_len = len(agent.conversation)

        agent._client.messages.create.side_effect = RuntimeError("network error")
        agent._call_llm("periodic", SAMPLE_PERCEPTION)

        # Conversation should be back to initial length (user message was popped)
        assert len(agent.conversation) == initial_len

    def test_system_prompt_contains_name(self):
        """_call_llm passes system prompt containing the Sim's name."""
        agent = self._make_agent(name="Gerry")
        text_response = _make_api_response([_make_text_block("ok")], stop_reason="end_turn")
        agent._client.messages.create.return_value = text_response

        agent._call_llm("periodic", SAMPLE_PERCEPTION)

        create_kwargs = agent._client.messages.create.call_args
        system = create_kwargs.kwargs.get("system", "")
        assert "Gerry" in system

    def test_end_turn_without_tool_does_not_loop(self):
        """If stop_reason is end_turn and no tools, client called exactly once."""
        agent = self._make_agent()
        text_response = _make_api_response([_make_text_block("thinking...")], stop_reason="end_turn")
        agent._client.messages.create.return_value = text_response

        agent._call_llm("periodic", SAMPLE_PERCEPTION)
        assert agent._client.messages.create.call_count == 1


# ---------------------------------------------------------------------------
# Integration tests — require ANTHROPIC_API_KEY
# ---------------------------------------------------------------------------

_AGENT_SCRIPT = str(SCRIPTS_DIR / "sim-agent-v3.py")

# Integration tests need a *real* API key (not a placeholder).
# The unit tests above inject "test-key-unit" via os.environ.setdefault — that
# is sufficient for mocked tests but not for subprocess calls to the real API.
# We detect a real key by checking it looks like a real Anthropic key (starts
# with "sk-ant-") or an explicit opt-in env var FREESIMS_RUN_INTEGRATION=1.
_RAW_KEY = os.environ.get("ANTHROPIC_API_KEY", "")
_REAL_KEY = _RAW_KEY.startswith("sk-ant-") or os.environ.get("FREESIMS_RUN_INTEGRATION", "") == "1"

_SKIP_REASON = (
    "Integration tests skipped: ANTHROPIC_API_KEY is not set to a real Anthropic key "
    "(expected 'sk-ant-...'). "
    "To run: export ANTHROPIC_API_KEY=<real-key> then re-run pytest. "
    "Alternatively set FREESIMS_RUN_INTEGRATION=1 to force the integration suite. "
    "Unit tests in TestSimAgentV3Unit run without a real key."
)


def _has_real_api_key() -> bool:
    return _REAL_KEY


def _build_perception_stream(n: int = 5, persist_id: int = 99) -> str:
    """Build n perception lines as JSONL."""
    lines = []
    for i in range(n):
        p = dict(SAMPLE_PERCEPTION)
        p["persist_id"] = persist_id
        p["name"] = "TestSim"
        p["clock"] = {"hours": 8, "minutes": i, "time_of_day": 1}
        p["motives"] = {
            "hunger": max(10, 75 - i * 5),
            "energy": 60,
            "comfort": 50,
            "hygiene": 80,
            "bladder": 90,
            "social": 40,
            "fun": 55,
            "mood": 65,
        }
        lines.append(json.dumps(p))
    return "\n".join(lines) + "\n"


@pytest.mark.skipif(not _has_real_api_key(), reason=_SKIP_REASON)
class TestSimAgentV3Integration:
    """Integration tests that call the real Anthropic API."""

    def _run_agent(self, stdin_data: str, timeout: int = 60, env_extra: dict = None):
        """Launch sim-agent-v3.py as a subprocess, feed stdin, collect output."""
        env = os.environ.copy()
        env["ANTHROPIC_API_KEY"] = os.environ["ANTHROPIC_API_KEY"]
        if env_extra:
            env.update(env_extra)

        proc = subprocess.run(
            [sys.executable, _AGENT_SCRIPT],
            input=stdin_data,
            capture_output=True,
            text=True,
            timeout=timeout,
            env=env,
        )
        return proc

    def test_produces_valid_json_on_stdout(self):
        """Agent produces ≥1 valid JSON line on stdout given 5 perceptions."""
        stream = _build_perception_stream(n=5, persist_id=77)
        proc = self._run_agent(stream, env_extra={"SIM_AGENT_NAME": "TestSim"})

        out_lines = [l.strip() for l in proc.stdout.splitlines() if l.strip()]
        assert len(out_lines) >= 1, (
            f"Expected ≥1 JSON line on stdout, got 0.\n"
            f"stderr: {proc.stderr[-2000:]}"
        )
        for line in out_lines:
            parsed = json.loads(line)  # raises if invalid
            assert "type" in parsed, f"IPC command missing 'type': {parsed}"

    def test_log_file_created(self):
        """Agent writes /tmp/embodied-agent-<persist_id>.jsonl."""
        persist_id = 77
        log_path = Path(f"/tmp/embodied-agent-{persist_id}.jsonl")
        if log_path.exists():
            log_path.unlink()

        stream = _build_perception_stream(n=5, persist_id=persist_id)
        self._run_agent(stream, env_extra={"SIM_AGENT_NAME": "TestSim"})

        assert log_path.exists(), f"Log file not created: {log_path}"
        lines = log_path.read_text().strip().splitlines()
        assert len(lines) >= 1, "Log file is empty"
        for line in lines:
            record = json.loads(line)
            assert "event" in record
            assert "ts" in record

    def test_at_least_one_tool_call_observed(self):
        """At least one IPC command emitted on stdout (tool dispatched)."""
        stream = _build_perception_stream(n=5, persist_id=78)
        proc = self._run_agent(stream, env_extra={"SIM_AGENT_NAME": "TestSim"})

        out_lines = [l.strip() for l in proc.stdout.splitlines() if l.strip()]
        assert len(out_lines) >= 1, (
            "Expected ≥1 IPC command (tool call) on stdout. "
            f"The LLM may have chosen text-only responses — check model or prompt.\n"
            f"stderr: {proc.stderr[-2000:]}"
        )

    def test_no_api_key_exits_with_error(self):
        """Without ANTHROPIC_API_KEY the agent exits non-zero with a clear message."""
        env = {k: v for k, v in os.environ.items() if k != "ANTHROPIC_API_KEY"}
        proc = subprocess.run(
            [sys.executable, _AGENT_SCRIPT],
            input="",
            capture_output=True,
            text=True,
            timeout=10,
            env=env,
        )
        assert proc.returncode != 0
        assert "ANTHROPIC_API_KEY" in proc.stderr
