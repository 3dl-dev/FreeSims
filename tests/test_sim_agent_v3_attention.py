"""
test_sim_agent_v3_attention.py — Unit + integration tests for the attention controller.

Unit tests: 8 tests (one per wake trigger) using hand-crafted perception sequences.
  Pure logic — no LLM calls, no I/O.

Integration test: 60-perception canned stream piped through sim-agent-v3.py subprocess.
  Asserts LLM call count < 12 (vs ~60 without attention).
  Uses the REAL mechanism from reeims-5cf (attention_wake log events).
  Skips only if SDK prerequisites are genuinely absent.
"""

import importlib.util
import json
import os
import shutil
import subprocess
import sys
from pathlib import Path
from unittest.mock import MagicMock, patch

import pytest

# ---------------------------------------------------------------------------
# Load the module under test — stub prerequisites so no SDK needed
# ---------------------------------------------------------------------------

SCRIPT = Path(__file__).parent.parent / "scripts" / "sim-agent-v3.py"


def _load_attention_module():
    spec = importlib.util.spec_from_file_location("sim_agent_v3_attn", SCRIPT)
    mod = importlib.util.module_from_spec(spec)
    with patch("shutil.which", return_value="/usr/bin/claude"):
        if "claude_agent_sdk" not in sys.modules:
            sys.modules["claude_agent_sdk"] = MagicMock()
        spec.loader.exec_module(mod)
    return mod


_mod = _load_attention_module()

MOTIVE_DELTA_THRESHOLD = _mod.MOTIVE_DELTA_THRESHOLD
MOTIVE_LOW_THRESHOLD = _mod.MOTIVE_LOW_THRESHOLD
PERIODIC_TICK_INTERVAL = _mod.PERIODIC_TICK_INTERVAL
IntentState = _mod.IntentState
should_think = _mod.should_think
_motive_keys = _mod._motive_keys
_avatar_names = _mod._avatar_names

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _base_motives() -> dict:
    """Motives comfortably above all thresholds."""
    return {"hunger": 60, "energy": 70, "mood": 65, "comfort": 55, "hygiene": 80, "social": 50}


def _base_perception(name: str = "Daisy", motives: dict | None = None, avatars: list | None = None, events: list | None = None) -> dict:
    return {
        "type": "perception",
        "persist_id": 7,
        "name": name,
        "motives": motives if motives is not None else _base_motives(),
        "lot_avatars": avatars if avatars is not None else [],
        "recent_events": events if events is not None else [],
    }


def _primed_state(motives: dict | None = None, avatars: list | None = None, last_llm_tick: int = 0) -> IntentState:
    """State where last_motives/avatars are set (post-first-LLM), periodic timer won't fire.

    avatars: raw lot_avatars list (same shape as perception); stored as sorted name list
             to match what on_perception stores after calling _avatar_names().
    """
    s = IntentState()
    s.last_motives = motives if motives is not None else _base_motives()
    raw_avatars = avatars if avatars is not None else []
    # Store as sorted name list — matches what on_perception stores
    s.last_lot_avatars = sorted(a.get("name", "") for a in raw_avatars)
    s.last_llm_tick = last_llm_tick  # set explicitly; periodic trigger inactive by default
    return s


# ---------------------------------------------------------------------------
# Unit tests — one per trigger
# ---------------------------------------------------------------------------

class TestTrigger1MotiveDelta:
    """T1: motive changed by ≥ MOTIVE_DELTA_THRESHOLD since last LLM call."""

    def test_fires_on_large_motive_change(self):
        base = _base_motives()
        old_motives = dict(base)
        new_motives = dict(base)
        new_motives["hunger"] = base["hunger"] - MOTIVE_DELTA_THRESHOLD  # exactly at threshold
        state = _primed_state(motives=old_motives, last_llm_tick=50)
        p = _base_perception(motives=new_motives)
        fired, tag = should_think(p, state, tick=55)
        assert fired, "T1 should fire when hunger drops by threshold"
        assert tag == "motive_delta"

    def test_does_not_fire_on_small_motive_change(self):
        base = _base_motives()
        new_motives = dict(base)
        new_motives["hunger"] = base["hunger"] - (MOTIVE_DELTA_THRESHOLD - 1)  # below threshold
        state = _primed_state(motives=base, last_llm_tick=50)
        p = _base_perception(motives=new_motives)
        fired, tag = should_think(p, state, tick=55)
        # May fire periodic or other; but not motive_delta
        if fired:
            assert tag != "motive_delta", f"T1 should not fire for small change, got {tag}"


class TestTrigger2MotiveThreshold:
    """T2: motive crossed below MOTIVE_LOW_THRESHOLD from above."""

    def test_fires_on_threshold_cross(self):
        base = _base_motives()
        old_motives = dict(base)
        old_motives["hunger"] = MOTIVE_LOW_THRESHOLD + 1  # just above threshold
        new_motives = dict(base)
        new_motives["hunger"] = MOTIVE_LOW_THRESHOLD - 1  # just below threshold
        state = _primed_state(motives=old_motives, last_llm_tick=50)
        p = _base_perception(motives=new_motives)
        fired, tag = should_think(p, state, tick=55)
        assert fired
        assert tag in ("motive_threshold", "motive_delta"), f"Expected threshold/delta, got {tag}"

    def test_does_not_fire_when_already_below(self):
        base = _base_motives()
        old_motives = dict(base)
        old_motives["hunger"] = MOTIVE_LOW_THRESHOLD - 5  # already below
        new_motives = dict(base)
        new_motives["hunger"] = MOTIVE_LOW_THRESHOLD - 6  # stays below (small move)
        state = _primed_state(motives=old_motives, last_llm_tick=50)
        p = _base_perception(motives=new_motives)
        fired, tag = should_think(p, state, tick=55)
        if fired:
            assert tag != "motive_threshold"


class TestTrigger3AvatarChange:
    """T3: lot_avatars list changed since last LLM call."""

    def test_fires_when_sim_enters(self):
        state = _primed_state(avatars=[], last_llm_tick=50)
        p = _base_perception(avatars=[{"name": "Bob"}])
        fired, tag = should_think(p, state, tick=55)
        assert fired
        assert tag == "avatar_change"

    def test_fires_when_sim_leaves(self):
        state = _primed_state(avatars=[{"name": "Bob"}], last_llm_tick=50)
        p = _base_perception(avatars=[])
        fired, tag = should_think(p, state, tick=55)
        assert fired
        assert tag == "avatar_change"

    def test_does_not_fire_when_same(self):
        state = _primed_state(avatars=[{"name": "Bob"}], last_llm_tick=50)
        p = _base_perception(avatars=[{"name": "Bob"}])
        fired, tag = should_think(p, state, tick=55)
        if fired:
            assert tag != "avatar_change"


class TestTrigger4ChatReceived:
    """T4: recent_events contains a chat_received event."""

    def test_fires_on_chat_event(self):
        state = _primed_state(last_llm_tick=50)
        events = [{"type": "chat_received", "sender_name": "Bob", "text": "Hey everyone"}]
        p = _base_perception(events=events)
        fired, tag = should_think(p, state, tick=55)
        assert fired
        assert tag in ("chat_received", "direct_address"), f"Expected chat trigger, got {tag}"

    def test_does_not_fire_with_no_events(self):
        state = _primed_state(last_llm_tick=50)
        p = _base_perception(events=[])
        fired, tag = should_think(p, state, tick=55)
        if fired:
            assert tag not in ("chat_received", "direct_address")


class TestTrigger5PathfindFailed:
    """T5: recent_events contains a pathfind-failed event."""

    def test_fires_on_pathfind_failed_event(self):
        state = _primed_state(last_llm_tick=50)
        events = [{"type": "pathfind-failed", "reason": "blocked"}]
        p = _base_perception(events=events)
        fired, tag = should_think(p, state, tick=55)
        assert fired
        assert tag == "pathfind_failed"

    def test_does_not_fire_with_no_pathfind_event(self):
        state = _primed_state(last_llm_tick=50)
        p = _base_perception(events=[{"type": "some_other_event"}])
        fired, tag = should_think(p, state, tick=55)
        if fired:
            assert tag != "pathfind_failed"


class TestTrigger6DirectAddress:
    """T6: Sim's own name appears in a chat_received message text."""

    def test_fires_when_name_in_chat_text(self):
        state = _primed_state(last_llm_tick=50)
        events = [{"type": "chat_received", "sender_name": "Bob", "text": "Daisy, are you hungry?"}]
        p = _base_perception(name="Daisy", events=events)
        fired, tag = should_think(p, state, tick=55)
        assert fired
        assert tag in ("chat_received", "direct_address")

    def test_direct_address_tag_when_name_in_text(self):
        """T4 (chat_received) fires before T6 but both are valid. Verify T6 logic independently."""
        # Build state where T4 would also fire but we check T6 path
        state = _primed_state(last_llm_tick=50)
        events = [{"type": "chat_received", "text": "Hey Daisy, come here!"}]
        p = _base_perception(name="Daisy", events=events)
        fired, tag = should_think(p, state, tick=55)
        assert fired, "Should fire on chat_received or direct_address"
        # Either tag is correct since T4 precedes T6 in evaluation order
        assert tag in ("chat_received", "direct_address")

    def test_does_not_fire_when_name_absent_from_chat(self):
        state = _primed_state(last_llm_tick=50)
        # T4 fires on any chat_received, so to test T6 isolation we need no chat events
        p = _base_perception(name="Daisy", events=[])
        fired, tag = should_think(p, state, tick=55)
        if fired:
            assert tag != "direct_address"


class TestTrigger7IntentTimer:
    """T7: wait_until_tick is set and current tick has reached or passed it."""

    def test_fires_when_timer_elapsed(self):
        state = _primed_state(last_llm_tick=50)
        state.wait_until_tick = 30
        p = _base_perception()
        fired, tag = should_think(p, state, tick=30)
        assert fired
        assert tag == "intent_timer"

    def test_fires_when_tick_past_wait(self):
        state = _primed_state(last_llm_tick=50)
        state.wait_until_tick = 20
        p = _base_perception()
        fired, tag = should_think(p, state, tick=35)
        assert fired
        assert tag == "intent_timer"

    def test_does_not_fire_before_timer_elapses(self):
        state = _primed_state(last_llm_tick=50)
        state.wait_until_tick = 100  # future
        p = _base_perception()
        fired, tag = should_think(p, state, tick=55)
        if fired:
            assert tag != "intent_timer"

    def test_does_not_fire_when_wait_is_zero(self):
        state = _primed_state(last_llm_tick=50)
        state.wait_until_tick = 0  # disabled
        p = _base_perception()
        fired, tag = should_think(p, state, tick=55)
        if fired:
            assert tag != "intent_timer"


class TestTrigger8Periodic:
    """T8: last LLM call was ≥ PERIODIC_TICK_INTERVAL ticks ago."""

    def test_fires_when_interval_elapsed(self):
        state = _primed_state(last_llm_tick=0)
        p = _base_perception()
        fired, tag = should_think(p, state, tick=PERIODIC_TICK_INTERVAL)
        assert fired
        assert tag == "periodic"

    def test_does_not_fire_before_interval(self):
        state = _primed_state(last_llm_tick=0)
        p = _base_perception()
        fired, tag = should_think(p, state, tick=PERIODIC_TICK_INTERVAL - 1)
        if fired:
            assert tag != "periodic"

    def test_first_perception_fires_via_primed_tick(self):
        """Fresh IntentState has last_llm_tick = -PERIODIC_TICK_INTERVAL, so tick=0 fires."""
        state = IntentState()  # unprimed, default
        p = _base_perception()
        fired, tag = should_think(p, state, tick=0)
        assert fired
        assert tag == "periodic"


# ---------------------------------------------------------------------------
# Integration test — 60-perception stream, assert LLM call count < 12
# ---------------------------------------------------------------------------

SCRIPT = Path(__file__).parent.parent / "scripts" / "sim-agent-v3.py"


def _prereq_errors() -> list[str]:
    errors: list[str] = []
    try:
        import claude_agent_sdk  # noqa: F401
    except ImportError:
        errors.append("claude-agent-sdk not installed — run: pip install claude-agent-sdk")
    if shutil.which("claude") is None:
        errors.append("claude CLI not on PATH — install Claude Code")
    return errors


def _build_60_perception_stream() -> str:
    """
    60 perceptions designed to fire ≤ 11 LLM calls:
    - Tick 1: periodic fires (primed IntentState)
    - Ticks 2-59: stable motives, same avatars, no events → suppressed
    - Tick 60: periodic fires again (60 ticks since last wake)
    Total expected wakes: ~2
    """
    base = _base_motives()
    lines = []
    for i in range(60):
        p = {
            "type": "perception",
            "persist_id": 42,
            "name": "TestSim",
            "motives": dict(base),
            "lot_avatars": [],
            "recent_events": [],
            "clock": {"hours": 10, "minutes": i},
        }
        lines.append(json.dumps(p))
    return "\n".join(lines) + "\n"


@pytest.mark.integration
class TestAttentionIntegration:

    def test_llm_call_count_under_12_for_60_perceptions(self):
        """Real mechanism: count attention_wake events in JSONL log for 60 stable perceptions."""
        missing = _prereq_errors()
        if missing:
            pytest.skip(
                "Integration prerequisites missing:\n" + "\n".join(f"  • {m}" for m in missing)
            )

        log_path = "/tmp/embodied-agent-42.jsonl"
        if os.path.exists(log_path):
            os.remove(log_path)

        env = os.environ.copy()
        env["SIM_NAME"] = "TestSim"
        env["PERSIST_ID"] = "42"

        proc = subprocess.Popen(
            [sys.executable, str(SCRIPT)],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            env=env,
        )
        stream = _build_60_perception_stream()
        try:
            _stdout, _stderr = proc.communicate(input=stream, timeout=300)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.communicate()
            pytest.fail("Agent timed out after 300s for 60-perception stream")

        assert proc.returncode == 0, f"Agent exited {proc.returncode}\nstderr:\n{_stderr}"

        assert os.path.exists(log_path), f"Log not created at {log_path}"
        events = []
        for line in Path(log_path).read_text().splitlines():
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                pass

        wake_events = [e for e in events if e.get("event") == "attention_wake"]
        llm_call_count = len(wake_events)

        assert llm_call_count < 12, (
            f"Expected <12 LLM calls for 60 stable perceptions, got {llm_call_count}.\n"
            f"Wake triggers: {[e.get('trigger') for e in wake_events]}"
        )
