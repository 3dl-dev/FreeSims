"""Tests for interact handler (reeims-3f8).

Unit tests:
  - Verb table: 10 verbs × expected fragment coverage
  - Target resolution: object_id path, name substr path, landmark fallback, None

Integration tests (mocked perception pipe):
  - "eat the fridge" colloquial: interact IPC emitted with fridge object_id + eat action_id
  - Unmapped verb ("juggle"): tool_result is the unknown-verb string
  - interact-complete event: await resolves
  - Too far: returns "too far"
"""

import asyncio
import importlib.util
import json
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

# ---------------------------------------------------------------------------
# Module loader (no SDK required)
# ---------------------------------------------------------------------------

SCRIPT = Path(__file__).parent.parent / "scripts" / "sim-agent-v3.py"


def _safe_load():
    spec = importlib.util.spec_from_file_location("sim_agent_v3", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    with patch("shutil.which", return_value="/usr/bin/claude"):
        if "claude_agent_sdk" not in sys.modules:
            sys.modules["claude_agent_sdk"] = MagicMock()
        spec.loader.exec_module(mod)
    return mod


_mod = _safe_load()
SimAgentV3 = _mod.SimAgentV3
ToolHandlers = _mod.ToolHandlers
IntentState = _mod.IntentState
ObjRef = _mod.ObjRef
build_landmark_table = _mod.build_landmark_table


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _make_agent(nearby=None, avatars=None, position=None) -> SimAgentV3:
    a = SimAgentV3.__new__(SimAgentV3)
    a.actor_uid = 7
    a.name = "Gerry"
    a.tick = 1
    a.memories = []
    a.intent = IntentState()
    a.log_path = "/tmp/test-interact-7.jsonl"
    a.walk_target = None
    a.walk_start_tick = 0
    a.walk_result = None
    a.walk_event = asyncio.Event()
    a.interact_object_id = 0
    a.interact_result = None
    a.interact_event = asyncio.Event()
    a.last_perception = {
        "persist_id": 7,
        "name": "Gerry",
        "position": position or {"x": 0, "y": 0, "level": 1},
        "nearby_objects": nearby or [],
        "lot_avatars": avatars or [],
        "recent_events": [],
    }
    a.handlers = ToolHandlers(a)
    return a


def _make_obj(name: str, oid: int, x: int, y: int, dist: float = 5.0,
              interactions=None) -> dict:
    return {
        "object_id": oid,
        "name": name,
        "position": {"x": x, "y": y, "level": 1},
        "distance": dist,
        "interactions": interactions or [],
    }


# Standard fridge with two interactions as seen from GetPieMenu().
FRIDGE_INTERACTIONS = [
    {"id": 0, "name": "Have Snack"},
    {"id": 1, "name": "Make Meal"},
]

TOILET_INTERACTIONS = [
    {"id": 0, "name": "Use"},
    {"id": 1, "name": "Unclog"},
]

BED_INTERACTIONS = [
    {"id": 0, "name": "Sleep"},
    {"id": 1, "name": "Nap"},
]

SHOWER_INTERACTIONS = [
    {"id": 0, "name": "Take Shower"},
]

SOFA_INTERACTIONS = [
    {"id": 0, "name": "Sit"},
    {"id": 1, "name": "Nap"},
]

TV_INTERACTIONS = [
    {"id": 0, "name": "Watch"},
    {"id": 1, "name": "Turn On"},
]

BOOKSHELF_INTERACTIONS = [
    {"id": 0, "name": "Read"},
    {"id": 1, "name": "Study"},
]

SINK_INTERACTIONS = [
    {"id": 0, "name": "Wash Hands"},
]

BAR_INTERACTIONS = [
    {"id": 0, "name": "Drink"},
    {"id": 1, "name": "Pour"},
]

PIANO_INTERACTIONS = [
    {"id": 0, "name": "Play"},
    {"id": 1, "name": "Practice"},
]


# ---------------------------------------------------------------------------
# Unit: verb table coverage — 10 verbs × expected fragment → action_id
# ---------------------------------------------------------------------------

class TestVerbTableCoverage:
    """Verify that each of the 10 required verbs resolves a real action_id
    against a representative object with matching pie menu entries."""

    def _agent_with_obj(self, interactions) -> tuple:
        obj = _make_obj("TestObject", 1, 3, 3, interactions=interactions)
        agent = _make_agent(nearby=[obj])
        handlers = agent.handlers
        return handlers, obj

    def test_eat_matches_have_snack(self):
        handlers, obj = self._agent_with_obj(FRIDGE_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "eat")
        assert aid == 0  # "Have Snack" is id=0

    def test_eat_matches_make_meal(self):
        handlers, obj = self._agent_with_obj([{"id": 2, "name": "Make Meal"}])
        aid = handlers._resolve_interact_action_id(obj, "eat")
        assert aid == 2

    def test_drink_matches_drink(self):
        handlers, obj = self._agent_with_obj(BAR_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "drink")
        assert aid == 0  # "Drink"

    def test_drink_matches_pour(self):
        handlers, obj = self._agent_with_obj([{"id": 5, "name": "Pour"}])
        aid = handlers._resolve_interact_action_id(obj, "drink")
        assert aid == 5

    def test_sit_matches_sit(self):
        handlers, obj = self._agent_with_obj(SOFA_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "sit")
        assert aid == 0  # "Sit"

    def test_sleep_matches_sleep(self):
        handlers, obj = self._agent_with_obj(BED_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "sleep")
        assert aid == 0  # "Sleep"

    def test_sleep_matches_nap(self):
        handlers, obj = self._agent_with_obj([{"id": 3, "name": "Nap"}])
        aid = handlers._resolve_interact_action_id(obj, "sleep")
        assert aid == 3

    def test_shower_matches_take_shower(self):
        handlers, obj = self._agent_with_obj(SHOWER_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "shower")
        assert aid == 0

    def test_use_toilet_matches_use(self):
        handlers, obj = self._agent_with_obj(TOILET_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "use-toilet")
        assert aid == 0  # "Use"

    def test_wash_matches_wash_hands(self):
        handlers, obj = self._agent_with_obj(SINK_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "wash")
        assert aid == 0  # "Wash Hands"

    def test_watch_matches_watch(self):
        handlers, obj = self._agent_with_obj(TV_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "watch")
        assert aid == 0  # "Watch"

    def test_read_matches_read(self):
        handlers, obj = self._agent_with_obj(BOOKSHELF_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "read")
        assert aid == 0  # "Read"

    def test_read_matches_study(self):
        handlers, obj = self._agent_with_obj([{"id": 7, "name": "Study"}])
        aid = handlers._resolve_interact_action_id(obj, "read")
        assert aid == 7

    def test_play_matches_play(self):
        handlers, obj = self._agent_with_obj(PIANO_INTERACTIONS)
        aid = handlers._resolve_interact_action_id(obj, "play")
        assert aid == 0  # "Play"

    def test_play_matches_practice(self):
        handlers, obj = self._agent_with_obj([{"id": 4, "name": "Practice"}])
        aid = handlers._resolve_interact_action_id(obj, "play")
        assert aid == 4

    def test_all_10_verbs_in_table(self):
        """Verify the 10 required verbs exist in VERB_FRAGMENTS."""
        handlers = _make_agent().handlers
        required = {"eat", "drink", "sit", "sleep", "shower", "use-toilet",
                    "wash", "watch", "read", "play"}
        assert required <= set(handlers.VERB_FRAGMENTS.keys())

    def test_unknown_verb_returns_none_from_resolver(self):
        handlers, obj = self._agent_with_obj(FRIDGE_INTERACTIONS)
        assert handlers._resolve_interact_action_id(obj, "juggle") is None

    def test_verb_no_fragment_match_returns_none(self):
        """Verb in table but object has no matching interaction name → None."""
        handlers, obj = self._agent_with_obj([{"id": 0, "name": "Wiggle"}])
        assert handlers._resolve_interact_action_id(obj, "eat") is None


# ---------------------------------------------------------------------------
# Unit: target resolution — both paths
# ---------------------------------------------------------------------------

class TestInteractTargetResolution:
    """_resolve_interact_target covers: numeric object_id, name substr, landmark fallback."""

    def test_numeric_object_id_path(self):
        """target='101' resolves by object_id."""
        obj = _make_obj("Refrigerator", 101, 5, 5, interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[obj])
        resolved = agent.handlers._resolve_interact_target("101", agent.last_perception)
        assert resolved is not None
        assert resolved["object_id"] == 101

    def test_numeric_object_id_not_found(self):
        obj = _make_obj("Refrigerator", 101, 5, 5)
        agent = _make_agent(nearby=[obj])
        assert agent.handlers._resolve_interact_target("999", agent.last_perception) is None

    def test_name_substr_path(self):
        """target='refrigerator' matches 'Refrigerator' via case-insensitive name."""
        obj = _make_obj("Refrigerator", 10, 5, 5, interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[obj])
        resolved = agent.handlers._resolve_interact_target("refrigerator", agent.last_perception)
        assert resolved is not None
        assert resolved["object_id"] == 10

    def test_exact_name_match(self):
        """target='Refrigerator' exact match."""
        obj = _make_obj("Refrigerator", 10, 5, 5, interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[obj])
        resolved = agent.handlers._resolve_interact_target("refrigerator", agent.last_perception)
        assert resolved is not None

    def test_landmark_fallback_path(self):
        """target='kitchen' → landmark table resolves to fridge object_id → obj found."""
        obj = _make_obj("Refrigerator", 55, 8, 8, interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[obj])
        resolved = agent.handlers._resolve_interact_target("kitchen", agent.last_perception)
        assert resolved is not None
        assert resolved["object_id"] == 55

    def test_unknown_target_returns_none(self):
        agent = _make_agent(nearby=[_make_obj("Refrigerator", 1, 5, 5)])
        assert agent.handlers._resolve_interact_target("moon", agent.last_perception) is None


# ---------------------------------------------------------------------------
# Integration: real mechanism
# ---------------------------------------------------------------------------

class TestInteractIntegration:
    def _run(self, coro):
        return asyncio.new_event_loop().run_until_complete(coro)

    def test_eat_fridge_emits_interact_ipc(self, capsys):
        """'eat the fridge' (colloquial): interact IPC emitted with fridge object_id + eat action_id."""
        fridge = _make_obj("Refrigerator", 42, 5, 5, dist=4.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])
        # Use handle_interact (sync) — emits IPC synchronously
        result = agent.handlers.handle_interact({"target": "refrigerator", "verb": "eat"})
        assert result.startswith("_interact_pending:")
        out = capsys.readouterr().out
        frames = [json.loads(ln) for ln in out.splitlines() if ln.strip()]
        interact_frames = [f for f in frames if f.get("type") == "interact"]
        assert len(interact_frames) >= 1, "No interact IPC emitted"
        f = interact_frames[0]
        assert f["object_id"] == 42, f"Expected object_id=42, got {f['object_id']}"
        # action_id should be 0 ("Have Snack") — first fragment match
        assert f["action_id"] == 0, f"Expected action_id=0 (Have Snack), got {f['action_id']}"
        assert f["actor_uid"] == 7

    def test_eat_fridge_via_landmark(self, capsys):
        """'eat' on 'kitchen' landmark → resolves to fridge via landmark table."""
        fridge = _make_obj("Refrigerator", 77, 9, 9, dist=3.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])
        result = agent.handlers.handle_interact({"target": "kitchen", "verb": "eat"})
        assert result.startswith("_interact_pending:")
        out = capsys.readouterr().out
        frames = [json.loads(ln) for ln in out.splitlines() if ln.strip()]
        interact_frames = [f for f in frames if f.get("type") == "interact"]
        assert len(interact_frames) >= 1
        assert interact_frames[0]["object_id"] == 77

    def test_unmapped_verb_juggle(self):
        """Unmapped verb 'juggle' → tool_result is the unknown-verb string."""
        fridge = _make_obj("Refrigerator", 42, 5, 5, dist=4.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])
        result = agent.handlers.handle_interact({"target": "refrigerator", "verb": "juggle"})
        assert result == "unknown verb: juggle"

    def test_unknown_target_cannot_find(self):
        agent = _make_agent()
        result = agent.handlers.handle_interact({"target": "castle", "verb": "eat"})
        assert "cannot find" in result and "castle" in result

    def test_too_far_returns_too_far(self):
        """Distance > 160 (>10 tiles) → returns 'too far'."""
        fridge = _make_obj("Refrigerator", 42, 100, 100, dist=200.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])
        result = agent.handlers.handle_interact({"target": "refrigerator", "verb": "eat"})
        assert result == "too far"

    def test_no_verb_uses_primary_interaction(self, capsys):
        """No verb → picks first interaction (primary affordance)."""
        fridge = _make_obj("Refrigerator", 42, 5, 5, dist=4.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])
        result = agent.handlers.handle_interact({"target": "refrigerator"})
        assert result.startswith("_interact_pending:")
        out = capsys.readouterr().out
        frames = [json.loads(ln) for ln in out.splitlines() if ln.strip()]
        interact_frames = [f for f in frames if f.get("type") == "interact"]
        assert interact_frames[0]["action_id"] == 0  # primary = id 0

    def test_interact_await_resolves_on_complete_event(self):
        """Async handler awaits interact-complete event and resolves."""
        fridge = _make_obj("Refrigerator", 42, 5, 5, dist=4.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])

        async def run():
            task = asyncio.create_task(
                agent.handlers.handle_interact_async({"target": "refrigerator", "verb": "eat"})
            )
            await asyncio.sleep(0)  # let handler emit and await
            # Deliver interact-complete
            agent._resolve_interact("done interacting")
            return await task

        result = asyncio.new_event_loop().run_until_complete(run())
        assert "done interacting" in result

    def test_interact_await_timeout(self):
        """If interact-complete never arrives, handler times out gracefully."""
        fridge = _make_obj("Refrigerator", 42, 5, 5, dist=4.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])

        async def run():
            # Patch wait_for to timeout immediately
            orig = asyncio.wait_for
            async def fast_timeout(coro, timeout):
                raise asyncio.TimeoutError()
            import asyncio as _aio
            _aio.wait_for = fast_timeout
            try:
                return await agent.handlers.handle_interact_async(
                    {"target": "refrigerator", "verb": "eat"}
                )
            finally:
                _aio.wait_for = orig

        result = asyncio.new_event_loop().run_until_complete(run())
        assert "timed out" in result

    def test_check_interact_complete_fires_on_event(self):
        """_check_interact_complete signals interact_event on interact-complete in recent_events."""
        fridge = _make_obj("Refrigerator", 42, 5, 5, dist=4.0,
                           interactions=FRIDGE_INTERACTIONS)
        agent = _make_agent(nearby=[fridge])
        agent.handlers.handle_interact({"target": "refrigerator", "verb": "eat"})
        assert agent.interact_object_id == 42  # pending
        complete_perception = dict(agent.last_perception)
        complete_perception["recent_events"] = [{"type": "interact-complete"}]
        agent._check_interact_complete(complete_perception)
        assert agent.interact_event.is_set()
        assert "done interacting" in (agent.interact_result or "")

    def test_interact_no_anthropic_import(self):
        """No bare 'import anthropic' or ANTHROPIC_API_KEY in sim-agent-v3.py."""
        import re
        src = SCRIPT.read_text()
        # Exclude comment lines from the match
        code_lines = [ln for ln in src.splitlines() if not ln.strip().startswith("#")]
        code = "\n".join(code_lines)
        assert not re.search(r"\bimport anthropic\b", code)
        assert "ANTHROPIC_API_KEY" not in code
