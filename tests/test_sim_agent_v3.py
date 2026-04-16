"""
Unit tests for sim-agent-v3.py tool dispatch.

Tests tool handlers directly — no LLM boundary crossed.
Mocks are OK here; we're validating IPC shape, not SDK integration.
"""

import importlib.util
import io
import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

# ---------------------------------------------------------------------------
# Load the module without executing main() or checking prerequisites
# ---------------------------------------------------------------------------

SCRIPT = Path(__file__).parent.parent / "scripts" / "sim-agent-v3.py"


def _load_module():
    spec = importlib.util.spec_from_file_location("sim_agent_v3", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    # Patch prerequisite check to no-op so we don't need SDK at import time
    with patch.object(spec.loader, "exec_module", wraps=spec.loader.exec_module):
        try:
            spec.loader.exec_module(mod)
        except SystemExit:
            pass
    return mod


# We need to stub out _check_prerequisites at exec time
_patched_stderr = io.StringIO()


def _safe_load():
    """Load module with _check_prerequisites stubbed out."""
    spec = importlib.util.spec_from_file_location("sim_agent_v3", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    # Inject a no-op check before executing the module
    mod._check_prerequisites = lambda: None  # type: ignore[attr-defined]
    # We need to exec the module source with _check_prerequisites already injected.
    # Easier: just patch shutil.which and the import at module level.
    with patch("shutil.which", return_value="/usr/bin/claude"):
        # Stub claude_agent_sdk import at the sys.modules level
        if "claude_agent_sdk" not in sys.modules:
            sys.modules["claude_agent_sdk"] = MagicMock()
        spec.loader.exec_module(mod)
    return mod


_mod = _safe_load()
SimAgentV3 = _mod.SimAgentV3
ToolHandlers = _mod.ToolHandlers
IntentState = _mod.IntentState


# ---------------------------------------------------------------------------
# Fixtures
# ---------------------------------------------------------------------------

@pytest.fixture
def agent():
    a = SimAgentV3.__new__(SimAgentV3)
    a.actor_uid = 42
    a.name = "Daisy"
    a.tick = 5
    a.last_perception = {
        "clock": {"hours": 9, "minutes": 30},
        "motives": {"hunger": 45, "energy": 70, "mood": 60},
        "nearby_objects": [{"name": "Refrigerator", "object_id": 101}],
        "lot_avatars": [{"name": "Gerry", "persist_id": 99}],
    }
    a.memories = []
    a.intent = IntentState()
    a.wait_until_tick = 0
    a.log_path = "/tmp/test-agent-42.jsonl"
    a.handlers = ToolHandlers(a)
    return a


# ---------------------------------------------------------------------------
# say — must emit chat IPC with correct actor_uid
# ---------------------------------------------------------------------------

class TestSayTool:
    def test_say_emits_chat_ipc(self, agent, capsys):
        result = agent.handlers.handle_say({"text": "Hello world"})
        captured = capsys.readouterr()
        lines = [l for l in captured.out.splitlines() if l.strip()]
        assert lines, "say must print at least one line to stdout"
        cmd = json.loads(lines[0])
        assert cmd["type"] == "chat"
        assert cmd["actor_uid"] == 42
        assert cmd["message"] == "Hello world"

    def test_say_truncates_to_140(self, agent, capsys):
        long_text = "x" * 200
        agent.handlers.handle_say({"text": long_text})
        captured = capsys.readouterr()
        cmd = json.loads(captured.out.splitlines()[0])
        assert len(cmd["message"]) <= 140

    def test_say_returns_confirmation(self, agent, capsys):
        result = agent.handlers.handle_say({"text": "Hi"})
        assert 'said:' in result

    def test_say_empty_text(self, agent, capsys):
        result = agent.handlers.handle_say({"text": ""})
        assert result == "said nothing"
        captured = capsys.readouterr()
        assert captured.out.strip() == ""


# ---------------------------------------------------------------------------
# wait — updates wait_until_tick, no IPC
# ---------------------------------------------------------------------------

class TestWaitTool:
    def test_wait_sets_wait_until_tick(self, agent, capsys):
        agent.tick = 10
        result = agent.handlers.handle_wait({"seconds": 30})
        assert agent.wait_until_tick == 40
        captured = capsys.readouterr()
        assert captured.out.strip() == "", "wait must NOT emit IPC"

    def test_wait_returns_string(self, agent):
        result = agent.handlers.handle_wait({"seconds": 5})
        assert "waiting" in result.lower()

    def test_wait_minimum_one_tick(self, agent):
        agent.tick = 7
        agent.handlers.handle_wait({"seconds": 0})
        assert agent.wait_until_tick >= 8


# ---------------------------------------------------------------------------
# remember — appends to memories, no IPC
# ---------------------------------------------------------------------------

class TestRememberTool:
    def test_remember_appends_to_memories(self, agent, capsys):
        agent.handlers.handle_remember({"note": "Gerry seemed angry"})
        assert "Gerry seemed angry" in agent.memories
        captured = capsys.readouterr()
        assert captured.out.strip() == "", "remember must NOT emit IPC"

    def test_remember_returns_confirmation(self, agent):
        result = agent.handlers.handle_remember({"note": "test note"})
        assert "remembered" in result.lower()

    def test_remember_empty_note(self, agent):
        result = agent.handlers.handle_remember({"note": ""})
        assert result == "nothing to remember"
        assert len(agent.memories) == 0


# ---------------------------------------------------------------------------
# look_around — no IPC, returns enriched snapshot string
# ---------------------------------------------------------------------------

class TestLookAroundTool:
    def test_look_around_returns_string(self, agent, capsys):
        result = agent.handlers.handle_look_around({})
        assert isinstance(result, str) and len(result) > 0
        captured = capsys.readouterr()
        assert captured.out.strip() == "", "look_around must NOT emit IPC"

    def test_look_around_includes_nearby_objects(self, agent):
        result = agent.handlers.handle_look_around({})
        assert "Refrigerator" in result

    def test_look_around_no_perception(self, agent):
        agent.last_perception = None
        result = agent.handlers.handle_look_around({})
        assert "nothing" in result.lower()


# ---------------------------------------------------------------------------
# walk_to stub — returns placeholder
# ---------------------------------------------------------------------------

class TestWalkToStub:
    def test_walk_to_returns_arrived(self, agent, capsys):
        result = agent.handlers.handle_walk_to({"target": "kitchen"})
        assert result == "arrived at kitchen"
        captured = capsys.readouterr()
        assert captured.out.strip() == "", "stub walk_to must NOT emit IPC"

    def test_walk_to_any_target(self, agent):
        result = agent.handlers.handle_walk_to({"target": "Gerry"})
        assert "arrived at Gerry" == result


# ---------------------------------------------------------------------------
# interact stub — returns placeholder
# ---------------------------------------------------------------------------

class TestInteractStub:
    def test_interact_returns_did(self, agent, capsys):
        result = agent.handlers.handle_interact({"target": "refrigerator", "verb": "eat"})
        assert result == "did eat refrigerator"
        captured = capsys.readouterr()
        assert captured.out.strip() == "", "stub interact must NOT emit IPC"

    def test_interact_default_verb(self, agent):
        result = agent.handlers.handle_interact({"target": "sofa"})
        assert "use" in result or "sofa" in result


# ---------------------------------------------------------------------------
# dispatch — routes by name, strips MCP prefix
# ---------------------------------------------------------------------------

class TestDispatch:
    def test_dispatch_say(self, agent, capsys):
        result = agent.handlers.dispatch("say", {"text": "test"})
        assert 'said:' in result

    def test_dispatch_strips_mcp_prefix(self, agent, capsys):
        # SDK names tools as mcp__<server>__<tool>
        result = agent.handlers.dispatch("mcp__sim_tools__say", {"text": "hi"})
        assert 'said:' in result

    def test_dispatch_unknown_tool(self, agent):
        result = agent.handlers.dispatch("nonexistent_tool", {})
        assert "unknown tool" in result

    @pytest.mark.parametrize("tool_name", ["say", "wait", "remember", "look_around", "walk_to", "interact"])
    def test_all_tools_callable_via_dispatch(self, agent, capsys, tool_name):
        args = {
            "say": {"text": "hello"},
            "wait": {"seconds": 1},
            "remember": {"note": "test"},
            "look_around": {},
            "walk_to": {"target": "kitchen"},
            "interact": {"target": "chair", "verb": "sit"},
        }[tool_name]
        result = agent.handlers.dispatch(tool_name, args)
        assert isinstance(result, str)


# ---------------------------------------------------------------------------
# No anthropic imports
# ---------------------------------------------------------------------------

def test_no_anthropic_import():
    import re
    text = SCRIPT.read_text()
    # Check actual import statements only (not comments about them)
    code_lines = [l for l in text.splitlines() if not l.lstrip().startswith("#")]
    code_text = "\n".join(code_lines)
    assert not re.search(r"^\s*import anthropic\b", code_text, re.MULTILINE), (
        "Found 'import anthropic' in code (not comment)"
    )
    assert "ANTHROPIC_API_KEY" not in code_text, (
        "Found ANTHROPIC_API_KEY in code"
    )
