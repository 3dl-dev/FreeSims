"""Tests for walk_to handler (reeims-039).

Unit tests:
  - build_landmark_table: 5+ categories, nearest wins, missing = absent
  - resolve_walk_target: tier (a) Sim name, tier (b) landmark, tier (c) None

Integration tests (real mechanism, mocked perception pipe):
  - walk to fridge: goto IPC emitted with correct coords; position match satisfies await
  - pathfind-failed: tool_result = "blocked: <reason>"
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
LANDMARK_CATEGORIES = _mod.LANDMARK_CATEGORIES


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
    a.log_path = "/tmp/test-walk-7.jsonl"
    a.walk_target = None
    a.walk_start_tick = 0
    a.walk_result = None
    a.walk_event = asyncio.Event()
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


def _make_obj(name: str, oid: int, x: int, y: int, dist: float = 5.0) -> dict:
    return {"object_id": oid, "name": name, "position": {"x": x, "y": y, "level": 1}, "distance": dist}


# ---------------------------------------------------------------------------
# Unit: build_landmark_table
# ---------------------------------------------------------------------------

class TestBuildLandmarkTable:
    def test_kitchen_maps_refrigerator(self):
        objs = [_make_obj("Refrigerator", 1, 10, 10)]
        t = build_landmark_table(objs)
        assert "kitchen" in t
        assert t["kitchen"].x == 10

    def test_bathroom_maps_toilet(self):
        objs = [_make_obj("Toilet - Standard", 2, 5, 5)]
        t = build_landmark_table(objs)
        assert "bathroom" in t
        assert t["bathroom"].object_id == 2

    def test_bedroom_maps_bed(self):
        objs = [_make_obj("Bed - Double", 3, 8, 12)]
        t = build_landmark_table(objs)
        assert "bedroom" in t
        assert t["bedroom"].name == "Bed - Double"

    def test_living_room_maps_sofa(self):
        objs = [_make_obj("Sofa - Comfy", 4, 3, 3)]
        t = build_landmark_table(objs)
        assert "living_room" in t

    def test_front_door_maps_door(self):
        objs = [_make_obj("Front Door", 5, 0, 14)]
        t = build_landmark_table(objs)
        assert "front_door" in t

    def test_stereo_maps_stereo(self):
        objs = [_make_obj("Stereo Hi-Fi", 6, 6, 6)]
        t = build_landmark_table(objs)
        assert "stereo" in t

    def test_absent_when_no_match(self):
        objs = [_make_obj("Lamp - Floor", 99, 1, 1)]
        t = build_landmark_table(objs)
        assert "kitchen" not in t
        assert "bathroom" not in t

    def test_nearest_wins(self):
        """Two fridges — closer one wins."""
        objs = [
            _make_obj("Refrigerator A", 1, 5, 5, dist=10.0),
            _make_obj("Refrigerator B", 2, 3, 3, dist=3.0),
        ]
        t = build_landmark_table(objs)
        assert t["kitchen"].object_id == 2

    def test_five_categories_from_one_scan(self):
        """Verify ≥5 categories resolve from a realistic nearby_objects list."""
        objs = [
            _make_obj("Refrigerator", 1, 10, 10, 5),
            _make_obj("Toilet", 2, 15, 8, 6),
            _make_obj("Bed - Single", 3, 8, 14, 7),
            _make_obj("Sofa", 4, 3, 3, 8),
            _make_obj("Door - Front", 5, 0, 10, 12),
        ]
        t = build_landmark_table(objs)
        assert len(t) >= 5

    def test_empty_nearby_returns_empty_table(self):
        assert build_landmark_table([]) == {}

    def test_obj_ref_fields_populated(self):
        objs = [_make_obj("Shower", 42, 7, 9)]
        t = build_landmark_table(objs)
        ref = t["bathroom"]
        assert ref.object_id == 42
        assert ref.x == 7
        assert ref.y == 9
        assert ref.level == 1
        assert ref.name == "Shower"


# ---------------------------------------------------------------------------
# Unit: 3-tier target resolution
# ---------------------------------------------------------------------------

class TestResolveWalkTarget:
    def test_tier_a_sim_name(self):
        """Tier (a): Sim name in lot_avatars resolves to that Sim's position."""
        agent = _make_agent(avatars=[
            {"name": "Daisy", "persist_id": 99, "position": {"x": 4, "y": 6, "level": 1}}
        ])
        ref = agent.handlers.resolve_walk_target("Daisy", agent.last_perception)
        assert ref is not None
        assert ref.x == 4
        assert ref.y == 6
        assert ref.object_id == 99

    def test_tier_a_case_insensitive(self):
        agent = _make_agent(avatars=[
            {"name": "Daisy", "persist_id": 99, "position": {"x": 4, "y": 6, "level": 1}}
        ])
        ref = agent.handlers.resolve_walk_target("daisy", agent.last_perception)
        assert ref is not None and ref.object_id == 99

    def test_tier_b_landmark(self):
        """Tier (b): landmark table resolves to nearest matching object."""
        agent = _make_agent(nearby=[_make_obj("Refrigerator", 5, 10, 12)])
        ref = agent.handlers.resolve_walk_target("kitchen", agent.last_perception)
        assert ref is not None
        assert ref.x == 10
        assert ref.object_id == 5

    def test_tier_c_unresolvable_returns_none(self):
        """Tier (c): unknown target → None."""
        agent = _make_agent()
        ref = agent.handlers.resolve_walk_target("moon", agent.last_perception)
        assert ref is None

    def test_tier_a_takes_priority_over_landmark(self):
        """Sim named 'kitchen' wins over landmark table."""
        agent = _make_agent(
            nearby=[_make_obj("Refrigerator", 5, 10, 12)],
            avatars=[{"name": "kitchen", "persist_id": 77, "position": {"x": 2, "y": 2, "level": 1}}],
        )
        ref = agent.handlers.resolve_walk_target("kitchen", agent.last_perception)
        assert ref is not None and ref.object_id == 77

    def test_error_message_lists_known_targets(self, capsys):
        agent = _make_agent(avatars=[
            {"name": "Daisy", "persist_id": 99, "position": {"x": 0, "y": 0, "level": 1}}
        ])
        result = agent.handlers.handle_walk_to({"target": "dungeon"})
        assert "error:" in result
        assert "dungeon" in result
        assert "Daisy" in result or "kitchen" in result


# ---------------------------------------------------------------------------
# Integration: real mechanism, mocked perception pipe
# ---------------------------------------------------------------------------

class TestWalkToIntegration:
    def _run(self, coro):
        return asyncio.new_event_loop().run_until_complete(coro)

    def test_goto_ipc_emitted_for_fridge(self, capsys):
        """walk_to fridge: goto IPC emitted with correct coords."""
        agent = _make_agent(nearby=[_make_obj("Refrigerator", 5, 10, 12)])
        # Simulate arrival by signalling the event synchronously after emit
        async def run():
            task = asyncio.create_task(agent.handlers.handle_walk_to_async({"target": "kitchen"}))
            await asyncio.sleep(0)  # let handler emit goto and await
            # Feed a perception with matching position (within 2 tiles of 10,12)
            arrival_perception = dict(agent.last_perception)
            arrival_perception["position"] = {"x": 10, "y": 12, "level": 1}
            agent.last_perception = arrival_perception
            agent._check_walk_arrival(arrival_perception)
            return await task
        result = self._run(run())
        assert "arrived at" in result  # name may be object name or landmark alias
        out = capsys.readouterr().out
        frames = [json.loads(l) for l in out.splitlines() if l.strip()]
        goto_frames = [f for f in frames if f.get("type") == "goto"]
        assert len(goto_frames) >= 1
        gf = goto_frames[0]
        assert gf["x"] == 10
        assert gf["y"] == 12
        assert gf["actor_uid"] == 7

    def test_pathfind_failed_returns_blocked(self):
        """Feed pathfind-failed event → tool_result starts with 'blocked:'."""
        agent = _make_agent(nearby=[_make_obj("Refrigerator", 5, 10, 12)])
        async def run():
            task = asyncio.create_task(agent.handlers.handle_walk_to_async({"target": "kitchen"}))
            await asyncio.sleep(0)
            # Deliver pathfind-failed via on_pathfind_failed
            agent.on_pathfind_failed({"type": "pathfind-failed", "reason": "obstacle"})
            return await task
        result = self._run(run())
        assert result.startswith("blocked:") or "blocked" in result
        assert "obstacle" in result

    def test_timeout_returns_timed_out(self):
        """If walk tick timeout reached, _check_walk_arrival sets timed-out result."""
        agent = _make_agent(nearby=[_make_obj("Refrigerator", 5, 10, 12)])
        async def run():
            task = asyncio.create_task(agent.handlers.handle_walk_to_async({"target": "kitchen"}))
            await asyncio.sleep(0)
            # Simulate 30+ ticks elapsed without arrival
            agent.walk_start_tick = agent.tick - 30
            timeout_perc = dict(agent.last_perception)
            timeout_perc["position"] = {"x": 0, "y": 0, "level": 1}  # still far away
            agent._check_walk_arrival(timeout_perc)  # should fire timeout
            return await task
        result = self._run(run())
        assert "timed out" in result

    def test_tier_c_error_no_ipc(self, capsys):
        """Unresolvable target: error returned, no goto IPC emitted."""
        agent = _make_agent()
        async def run():
            return await agent.handlers.handle_walk_to_async({"target": "dungeon"})
        result = self._run(run())
        assert "error:" in result
        out = capsys.readouterr().out
        frames = [json.loads(l) for l in out.splitlines() if l.strip()]
        assert not any(f.get("type") == "goto" for f in frames)

    def test_check_walk_arrival_pathfind_in_recent_events(self):
        """_check_walk_arrival fires on pathfind-failed in recent_events."""
        agent = _make_agent(nearby=[_make_obj("Toilet", 3, 5, 5)])
        agent.handlers.handle_walk_to({"target": "bathroom"})
        pf_perception = dict(agent.last_perception)
        pf_perception["recent_events"] = [{"type": "pathfind-failed", "reason": "wall"}]
        agent._check_walk_arrival(pf_perception)
        assert agent.walk_event.is_set()
        assert "blocked" in (agent.walk_result or "")

    def test_check_walk_arrival_position_match(self):
        """_check_walk_arrival fires when position matches dest within 2 tiles."""
        agent = _make_agent(nearby=[_make_obj("Sofa", 4, 3, 3)])
        agent.handlers.handle_walk_to({"target": "living_room"})
        arrive_perc = dict(agent.last_perception)
        arrive_perc["position"] = {"x": 3, "y": 3, "level": 1}  # exact match
        agent._check_walk_arrival(arrive_perc)
        assert agent.walk_event.is_set()
        assert "arrived" in (agent.walk_result or "")
