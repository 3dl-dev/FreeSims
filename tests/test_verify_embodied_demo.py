"""Tests for scripts/verify-embodied-demo.py.

Unit tests: each of 7 assertions tested with good + bad synthetic inputs.
Integration tests: synthetic good log (all-green) + bad log (god-mode leakage).
"""

import importlib.util
import json
import os
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any

import pytest

# ---------------------------------------------------------------------------
# Load the module under test without running main()
# ---------------------------------------------------------------------------

_SCRIPT = Path(__file__).parent.parent / "scripts" / "verify-embodied-demo.py"


def _load_module():
    spec = importlib.util.spec_from_file_location("verify_embodied_demo", _SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


vd = _load_module()


# ---------------------------------------------------------------------------
# Helpers for building synthetic event lists
# ---------------------------------------------------------------------------

def turn(tool=None, trigger=None, reasoning=None):
    ev = {"event": "turn"}
    if tool:
        ev["tool"] = tool
    if trigger:
        ev["trigger"] = trigger
    if reasoning:
        ev["reasoning"] = reasoning
    return ev


def ipc_out_chat(message="hello", actor_name="Gerry"):
    return {
        "event": "ipc_out",
        "cmd": {"type": "chat", "message": message, "actor_name": actor_name},
    }


def ipc_in_ok():
    return {"event": "ipc_in", "frame": {"type": "response", "status": "ok"}}


def ipc_in_error(msg="tool error"):
    return {"event": "ipc_in", "frame": {"type": "response", "status": "error", "message": msg}}


def ipc_in_pathfind_failed():
    return {"event": "ipc_in", "frame": {"type": "pathfind-failed"}}


def perception_clean(self_name="Gerry"):
    """Perception event with ONLY self in lot_avatars (no other-sim motives)."""
    return {
        "event": "perception",
        "lot_avatars": [
            {
                "name": self_name,
                "motives": {"hunger": 60, "energy": 70, "mood": 80},
                "position": {"x": 5, "y": 5},
            }
        ],
    }


def perception_with_other_motives(self_name="Gerry", other_name="Daisy"):
    """Perception event with another Sim's numeric motives — god-mode leakage."""
    return {
        "event": "perception",
        "lot_avatars": [
            {
                "name": self_name,
                "motives": {"hunger": 60, "energy": 70, "mood": 80},
            },
            {
                "name": other_name,
                "motives": {"hunger": 10, "energy": 20, "mood": 30},  # leakage
                "looks_like": "tired",
            },
        ],
    }


def good_log(self_name="Gerry", n_turns=5):
    """Minimal good log: 5 turns, 2 tools, no issues."""
    events = []
    tools = ["walk_to", "interact", "say", "remember", "wait"]
    for i in range(n_turns):
        events.append(turn(tool=tools[i % len(tools)]))
    events.append(ipc_out_chat("Hello there!", actor_name=self_name))
    events.append(ipc_in_ok())
    events.append(perception_clean(self_name))
    return events


def make_log_file(events: list[dict]) -> str:
    """Write events to a temp JSONL file, return path."""
    f = tempfile.NamedTemporaryFile(
        mode="w", suffix=".jsonl", delete=False, encoding="utf-8"
    )
    for ev in events:
        f.write(json.dumps(ev) + "\n")
    f.close()
    return f.name


# ---------------------------------------------------------------------------
# A1: min_turns
# ---------------------------------------------------------------------------

class TestAssertMinTurns:
    def test_good_exactly_5(self):
        events = [turn() for _ in range(5)]
        name, passed, detail = vd.assert_min_turns(events)
        assert passed, detail

    def test_good_more_than_5(self):
        events = [turn() for _ in range(10)]
        name, passed, detail = vd.assert_min_turns(events)
        assert passed, detail

    def test_bad_zero(self):
        events = []
        name, passed, detail = vd.assert_min_turns(events)
        assert not passed, detail

    def test_bad_four(self):
        events = [turn() for _ in range(4)]
        name, passed, detail = vd.assert_min_turns(events)
        assert not passed, detail

    def test_non_turn_events_not_counted(self):
        events = [ipc_in_ok() for _ in range(10)]  # no turn events
        name, passed, detail = vd.assert_min_turns(events)
        assert not passed, detail


# ---------------------------------------------------------------------------
# A2: distinct_tools
# ---------------------------------------------------------------------------

class TestAssertDistinctTools:
    def test_good_two_tools(self):
        events = [turn(tool="walk_to"), turn(tool="interact")]
        name, passed, detail = vd.assert_distinct_tools(events)
        assert passed, detail

    def test_good_many_tools(self):
        events = [turn(tool=t) for t in ["walk_to", "interact", "say", "wait"]]
        name, passed, detail = vd.assert_distinct_tools(events)
        assert passed, detail

    def test_bad_one_tool_repeated(self):
        events = [turn(tool="walk_to") for _ in range(5)]
        name, passed, detail = vd.assert_distinct_tools(events)
        assert not passed, detail

    def test_bad_no_tools(self):
        events = [turn() for _ in range(5)]  # turns with no tool field
        name, passed, detail = vd.assert_distinct_tools(events)
        assert not passed, detail


# ---------------------------------------------------------------------------
# A3: conversation_exchange
# ---------------------------------------------------------------------------

class TestAssertConversationExchange:
    def test_good_two_agents_exchange_with_timestamps(self):
        """Both agents have say events with timestamps close together."""
        events_a = [
            {
                "event": "ipc_out",
                "ts": "2026-04-16T10:00:00Z",
                "cmd": {"type": "chat", "message": "Hey Daisy!", "actor_name": "Gerry"},
            }
        ]
        events_b = [
            {
                "event": "ipc_out",
                "ts": "2026-04-16T10:00:05Z",
                "cmd": {"type": "chat", "message": "Hey Gerry!", "actor_name": "Daisy"},
            }
        ]
        all_logs = {"a.jsonl": events_a, "b.jsonl": events_b}
        name, passed, detail = vd.assert_conversation_exchange(all_logs)
        assert passed, detail

    def test_good_two_agents_exchange_no_timestamps(self):
        """Both agents have say events — exchange inferred without timestamps."""
        events_a = [ipc_out_chat("Hey Daisy!", "Gerry")]
        events_b = [ipc_out_chat("Hey Gerry!", "Daisy")]
        all_logs = {"a.jsonl": events_a, "b.jsonl": events_b}
        name, passed, detail = vd.assert_conversation_exchange(all_logs)
        assert passed, detail

    def test_bad_single_agent(self):
        events = good_log("Gerry")
        all_logs = {"a.jsonl": events}
        name, passed, detail = vd.assert_conversation_exchange(all_logs)
        assert not passed

    def test_bad_no_reply(self):
        # A says, B has no say events
        events_a = [ipc_out_chat("Hello?", "Gerry")]
        events_b = [turn() for _ in range(10)]  # no ipc_out chat
        all_logs = {"a.jsonl": events_a, "b.jsonl": events_b}
        name, passed, detail = vd.assert_conversation_exchange(all_logs)
        assert not passed, detail

    def test_bad_no_say_events_at_all(self):
        """Neither agent has any say events — fails."""
        events_a = [turn() for _ in range(5)]
        events_b = [turn() for _ in range(5)]
        all_logs = {"a.jsonl": events_a, "b.jsonl": events_b}
        name, passed, detail = vd.assert_conversation_exchange(all_logs)
        assert not passed, detail


# ---------------------------------------------------------------------------
# A4: perception_hygiene
# ---------------------------------------------------------------------------

class TestAssertPerceptionHygiene:
    def test_good_no_leakage(self):
        events = [perception_clean("Gerry"), ipc_in_ok()]
        name, passed, detail = vd.assert_perception_hygiene(events, "Gerry")
        assert passed, detail

    def test_bad_other_sim_motives_in_lot_avatars(self):
        events = [perception_with_other_motives("Gerry", "Daisy")]
        name, passed, detail = vd.assert_perception_hygiene(events, "Gerry")
        assert not passed, detail

    def test_good_self_motives_allowed(self):
        # Self-motives should NOT trigger the check
        events = [perception_clean("Gerry")]
        name, passed, detail = vd.assert_perception_hygiene(events, "Gerry")
        assert passed, detail

    def test_bad_other_sim_motives_in_turn_text(self):
        # Turn reasoning text contains another Sim's motive values
        text = 'I see Daisy {"name":"Daisy","hunger":15} in the room'
        events = [turn(reasoning=text)]
        name, passed, detail = vd.assert_perception_hygiene(events, "Gerry")
        assert not passed, detail

    def test_good_empty_lot_avatars(self):
        events = [{"event": "perception", "lot_avatars": []}]
        name, passed, detail = vd.assert_perception_hygiene(events, "Gerry")
        assert passed, detail


# ---------------------------------------------------------------------------
# A5: identity_first_person
# ---------------------------------------------------------------------------

class TestAssertIdentityFirstPerson:
    def test_good_no_third_person(self):
        events = [turn(reasoning="I feel hungry. I should find some food.")]
        name, passed, detail = vd.assert_identity_first_person(events, "Gerry")
        assert passed, detail

    def test_bad_more_than_two_occurrences(self):
        text = "Gerry will eat. Gerry will sleep. Gerry will shower. Gerry will work."
        events = [turn(reasoning=text)]
        name, passed, detail = vd.assert_identity_first_person(events, "Gerry")
        assert not passed, detail

    def test_warn_at_two_or_fewer(self):
        text = "Gerry will eat. Gerry will sleep."
        events = [turn(reasoning=text)]
        name, passed, detail = vd.assert_identity_first_person(events, "Gerry", strict=False)
        # count is exactly 2 — should PASS (threshold is >2)
        assert passed, detail

    def test_strict_same_count_fails(self):
        # With strict mode, > 2 is still the threshold, but let's check 3 fails
        text = "Gerry will eat. Gerry will sleep. Gerry will shower."
        events = [turn(reasoning=text)]
        name, passed, detail = vd.assert_identity_first_person(events, "Gerry", strict=True)
        assert not passed, detail

    def test_unknown_self_name_skips(self):
        events = [turn(reasoning="I will eat.")]
        name, passed, detail = vd.assert_identity_first_person(events, "unknown")
        assert passed, "should skip when name is unknown"

    def test_case_insensitive(self):
        text = "gerry will eat. gerry will sleep. gerry will shower."
        events = [turn(reasoning=text)]
        name, passed, detail = vd.assert_identity_first_person(events, "Gerry")
        assert not passed, detail


# ---------------------------------------------------------------------------
# A6: pathfind_resolved
# ---------------------------------------------------------------------------

class TestAssertPathfindResolved:
    def test_good_no_pathfind_failures(self):
        events = [turn() for _ in range(5)]
        name, passed, detail = vd.assert_pathfind_resolved(events)
        assert passed, detail

    def test_good_failure_resolved_within_3_turns(self):
        events = [
            ipc_in_pathfind_failed(),
            turn(trigger="pathfind_failed"),
        ]
        name, passed, detail = vd.assert_pathfind_resolved(events)
        assert passed, detail

    def test_good_failure_resolved_at_turn_3(self):
        events = [
            ipc_in_pathfind_failed(),
            turn(),  # turn 1 — no pathfind trigger
            turn(),  # turn 2
            turn(trigger="pathfind_failed"),  # turn 3 — resolved
        ]
        name, passed, detail = vd.assert_pathfind_resolved(events)
        assert passed, detail

    def test_bad_failure_unresolved(self):
        events = [
            ipc_in_pathfind_failed(),
            turn(),
            turn(),
            turn(),
            turn(),  # 4 turns, no pathfind_failed trigger
        ]
        name, passed, detail = vd.assert_pathfind_resolved(events)
        assert not passed, detail

    def test_bad_failure_resolved_too_late(self):
        events = [
            ipc_in_pathfind_failed(),
            turn(),
            turn(),
            turn(),
            turn(trigger="pathfind_failed"),  # turn 4 — too late
        ]
        name, passed, detail = vd.assert_pathfind_resolved(events)
        assert not passed, detail

    def test_explicit_pathfind_failed_event(self):
        events = [
            {"event": "pathfind-failed"},
            turn(trigger="pathfind_failed"),
        ]
        name, passed, detail = vd.assert_pathfind_resolved(events)
        assert passed, detail


# ---------------------------------------------------------------------------
# A7: steady_state
# ---------------------------------------------------------------------------

class TestAssertSteadyState:
    def test_good_no_errors(self):
        events = [ipc_in_ok() for _ in range(9)]
        name, passed, detail = vd.assert_steady_state(events)
        assert passed, detail

    def test_good_errors_only_in_early_portion(self):
        events = (
            [ipc_in_error("early error")] * 3  # first third
            + [ipc_in_ok()] * 3               # middle third
            + [ipc_in_ok()] * 3               # last third — clean
        )
        name, passed, detail = vd.assert_steady_state(events)
        assert passed, detail

    def test_bad_error_in_last_third(self):
        events = (
            [ipc_in_ok()] * 6
            + [ipc_in_error("late error")]  # in last third
        )
        name, passed, detail = vd.assert_steady_state(events)
        assert not passed, detail

    def test_bad_empty(self):
        name, passed, detail = vd.assert_steady_state([])
        assert not passed

    def test_single_event_error(self):
        events = [ipc_in_error("only event")]
        name, passed, detail = vd.assert_steady_state(events)
        assert not passed, detail


# ---------------------------------------------------------------------------
# Integration: synthetic good log (all green)
# ---------------------------------------------------------------------------

class TestIntegrationGoodLog:
    def test_good_log_all_pass(self, tmp_path):
        """Synthetic good log with two agents passes all assertions."""
        # Agent A
        events_a = (
            [turn(tool=t) for t in ["walk_to", "interact", "say", "remember", "wait"]]
            + [ipc_out_chat("Hey Daisy!", "Gerry")]
            + [ipc_in_ok() for _ in range(6)]
            + [perception_clean("Gerry")]
        )
        # Agent B replies quickly
        events_b = (
            [turn(tool=t) for t in ["walk_to", "say", "look_around", "wait", "interact"]]
            + [ipc_out_chat("Hey Gerry!", "Daisy")]
            + [ipc_in_ok() for _ in range(6)]
            + [perception_clean("Daisy")]
        )

        log_a = str(tmp_path / "agent_a.jsonl")
        log_b = str(tmp_path / "agent_b.jsonl")
        with open(log_a, "w") as f:
            for ev in events_a:
                f.write(json.dumps(ev) + "\n")
        with open(log_b, "w") as f:
            for ev in events_b:
                f.write(json.dumps(ev) + "\n")

        all_logs = {log_a: events_a, log_b: events_b}

        for path, events in [(log_a, events_a), (log_b, events_b)]:
            self_name = "Gerry" if path == log_a else "Daisy"
            results = vd.run_assertions(path, events, self_name, all_logs, strict=False)
            for name, passed, detail in results:
                # A3 (exchange) should pass since both agents have says close together
                # A5 (identity) passes since no 'X will' text
                assert passed, f"{name} FAILED for {path}: {detail}"


# ---------------------------------------------------------------------------
# Integration: synthetic bad log (god-mode leakage → red)
# ---------------------------------------------------------------------------

class TestIntegrationBadLog:
    def test_hygiene_failure_detected(self, tmp_path):
        """Log with god-mode other-Sim motive data fails assertion A4."""
        events = (
            [turn(tool=t) for t in ["walk_to", "interact", "say", "remember", "wait"]]
            + [ipc_out_chat("Hi", "Gerry")]
            + [ipc_in_ok()]
            + [perception_with_other_motives("Gerry", "Daisy")]  # leakage
        )
        log = str(tmp_path / "agent_leak.jsonl")
        with open(log, "w") as f:
            for ev in events:
                f.write(json.dumps(ev) + "\n")

        all_logs = {log: events}
        results = vd.run_assertions(log, events, "Gerry", all_logs, strict=False)

        a4 = {name: (passed, detail) for name, passed, detail in results}
        passed, detail = a4["A4:perception_hygiene"]
        assert not passed, f"Expected A4 to FAIL but got: {detail}"
        assert "leakage" in detail.lower() or "motive" in detail.lower(), detail

    def test_identity_failure_detected(self, tmp_path):
        """Log with third-person self-references fails assertion A5 in strict mode."""
        text = "Gerry will eat. Gerry will sleep. Gerry will shower. Gerry will work."
        events = (
            [turn(tool=t, reasoning=text if i == 0 else None)
             for i, t in enumerate(["walk_to", "interact", "say", "remember", "wait"])]
            + [ipc_out_chat("Hi", "Gerry")]
            + [ipc_in_ok()]
        )
        log = str(tmp_path / "agent_id.jsonl")
        with open(log, "w") as f:
            for ev in events:
                f.write(json.dumps(ev) + "\n")

        all_logs = {log: events}
        results = vd.run_assertions(log, events, "Gerry", all_logs, strict=True)
        a5 = {name: (passed, detail) for name, passed, detail in results}
        passed, detail = a5["A5:identity_first_person"]
        assert not passed, f"Expected A5 to FAIL but got: {detail}"


# ---------------------------------------------------------------------------
# CLI integration: run script as subprocess
# ---------------------------------------------------------------------------

class TestCLI:
    def test_cli_exits_0_on_good_log(self, tmp_path):
        """Script exits 0 for a good two-agent log."""
        events_a = (
            [turn(tool=t) for t in ["walk_to", "interact", "say", "remember", "wait"]]
            + [ipc_out_chat("Hey!", "Gerry")]
            + [ipc_in_ok() for _ in range(5)]
            + [perception_clean("Gerry")]
        )
        events_b = (
            [turn(tool=t) for t in ["walk_to", "say", "interact", "remember", "wait"]]
            + [ipc_out_chat("Hey back!", "Daisy")]
            + [ipc_in_ok() for _ in range(5)]
            + [perception_clean("Daisy")]
        )
        log_a = str(tmp_path / "a.jsonl")
        log_b = str(tmp_path / "b.jsonl")
        with open(log_a, "w") as f:
            for ev in events_a:
                f.write(json.dumps(ev) + "\n")
        with open(log_b, "w") as f:
            for ev in events_b:
                f.write(json.dumps(ev) + "\n")

        result = subprocess.run(
            [sys.executable, str(_SCRIPT), log_a, log_b],
            capture_output=True, text=True
        )
        assert result.returncode == 0, (
            f"Expected exit 0, got {result.returncode}\n"
            f"stdout: {result.stdout}\nstderr: {result.stderr}"
        )

    def test_cli_exits_1_on_bad_log(self, tmp_path):
        """Script exits 1 for a log with god-mode leakage."""
        events = (
            [turn(tool=t) for t in ["walk_to", "interact", "say", "remember", "wait"]]
            + [ipc_out_chat("Hi", "Gerry")]
            + [ipc_in_ok()]
            + [perception_with_other_motives("Gerry", "Daisy")]
        )
        log = str(tmp_path / "bad.jsonl")
        with open(log, "w") as f:
            for ev in events:
                f.write(json.dumps(ev) + "\n")

        result = subprocess.run(
            [sys.executable, str(_SCRIPT), log],
            capture_output=True, text=True
        )
        assert result.returncode == 1, (
            f"Expected exit 1, got {result.returncode}\n"
            f"stdout: {result.stdout}\nstderr: {result.stderr}"
        )

    def test_cli_json_output(self, tmp_path):
        """--json flag produces valid JSON to stderr."""
        events = (
            [turn(tool=t) for t in ["walk_to", "interact", "say", "remember", "wait"]]
            + [ipc_out_chat("Hi", "Gerry")]
            + [ipc_in_ok() for _ in range(5)]
            + [perception_clean("Gerry")]
        )
        log = str(tmp_path / "ok.jsonl")
        with open(log, "w") as f:
            for ev in events:
                f.write(json.dumps(ev) + "\n")

        result = subprocess.run(
            [sys.executable, str(_SCRIPT), log, "--json"],
            capture_output=True, text=True
        )
        data = json.loads(result.stderr)
        assert "overall" in data
        assert "agents" in data
        assert len(data["agents"]) == 1

    def test_cli_missing_file_exits_1(self, tmp_path):
        result = subprocess.run(
            [sys.executable, str(_SCRIPT), "/tmp/does_not_exist_xyz.jsonl"],
            capture_output=True, text=True
        )
        assert result.returncode == 1
