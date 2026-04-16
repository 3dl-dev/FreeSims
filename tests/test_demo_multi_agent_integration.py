"""Integration smoke test for scripts/demo-multi-agent.sh with v3 agent.

Gated by FREESIMS_INTEGRATION=1. Without that env var, the integration tests
are skipped (not collected). Default pytest run stays green.

Tests:
  - demo-multi-agent.sh --sim1-name Alice --sim2-name Bob launches without
    immediately crashing (basic process lifecycle).
  - After 90s, /tmp/embodied-agent-*.jsonl files exist with ≥1 turn or perception
    record each (proves agents received perceptions and produced LLM turns).
  - No orphaned 'claude' CLI or 'sim-agent-v3' python processes after harness exits.
  - FREESIMS_AGENT_VERSION=v2 fallback path: script selects sim-agent-v2.py correctly.

Orphan check: after cleanup, assert pgrep -f 'claude --print' and
pgrep -f 'sim-agent-v3' return no matches.
"""

import json
import os
import shutil
import signal
import subprocess
import time
from pathlib import Path

import pytest

REPO_ROOT = Path(__file__).parent.parent
HARNESS = REPO_ROOT / "scripts" / "demo-multi-agent.sh"
AGENT_V3 = REPO_ROOT / "scripts" / "sim-agent-v3.py"
AGENT_V2 = REPO_ROOT / "scripts" / "sim-agent-v2.py"

# Gate: integration tests only run when FREESIMS_INTEGRATION=1
INTEGRATION = os.environ.get("FREESIMS_INTEGRATION", "0") == "1"
SKIP_REASON = "Set FREESIMS_INTEGRATION=1 to run real harness tests"

requires_integration = pytest.mark.skipif(not INTEGRATION, reason=SKIP_REASON)


# ---------------------------------------------------------------------------
# Unit tests (always run — no FREESIMS_INTEGRATION required)
# ---------------------------------------------------------------------------


def test_harness_script_exists():
    """The harness script must exist and be executable."""
    assert HARNESS.exists(), f"Missing: {HARNESS}"
    assert os.access(HARNESS, os.X_OK), f"Not executable: {HARNESS}"


def test_agent_v3_exists():
    """sim-agent-v3.py must exist."""
    assert AGENT_V3.exists(), f"Missing: {AGENT_V3}"


def test_agent_v2_preserved():
    """sim-agent-v2.py must NOT be removed (v2 fallback constraint)."""
    assert AGENT_V2.exists(), f"sim-agent-v2.py was removed — must be preserved as fallback"


def test_harness_selects_v3_by_default():
    """AGENT_SCRIPT in harness defaults to sim-agent-v3.py when FREESIMS_AGENT_VERSION is unset."""
    content = HARNESS.read_text()
    # The harness should reference v3 as default
    assert "sim-agent-v3.py" in content, "Harness does not reference sim-agent-v3.py"
    # And must preserve v2 selection path
    assert "sim-agent-v2.py" in content, "Harness v2 fallback path removed — must be preserved"


def test_harness_version_toggle_logic():
    """Harness must contain FREESIMS_AGENT_VERSION toggle logic."""
    content = HARNESS.read_text()
    assert "FREESIMS_AGENT_VERSION" in content, "Missing FREESIMS_AGENT_VERSION toggle"
    # v3 is the default
    assert 'FREESIMS_AGENT_VERSION:-v3' in content or 'FREESIMS_AGENT_VERSION="v3"' in content or \
           "FREESIMS_AGENT_VERSION:=v3" in content or "default.*v3" in content.lower() or \
           ':-v3}' in content, \
        "FREESIMS_AGENT_VERSION default must be v3"


def test_harness_cleanup_kills_claude_subprocesses():
    """Cleanup section must reference claude --print to kill Agent SDK subprocesses."""
    content = HARNESS.read_text()
    assert "claude --print" in content, \
        "cleanup() must kill 'claude --print' processes spawned by Agent SDK"


def test_harness_env_vars_for_v3():
    """Harness must pass SIM_NAME env var (not SIM_AGENT_NAME) when launching v3."""
    content = HARNESS.read_text()
    # In v3 branch, SIM_NAME= must be set (used by sim-agent-v3.py)
    assert 'SIM_NAME=' in content, \
        "Harness must set SIM_NAME env var for v3 agents (not SIM_AGENT_NAME only)"


def test_v3_log_path_documented():
    """Harness must document the v3 log path /tmp/embodied-agent-*.jsonl."""
    content = HARNESS.read_text()
    assert "embodied-agent" in content, \
        "Harness must reference /tmp/embodied-agent-*.jsonl (v3 log path)"


# ---------------------------------------------------------------------------
# Integration tests (gated by FREESIMS_INTEGRATION=1)
# ---------------------------------------------------------------------------


@requires_integration
def test_harness_v3_produces_agent_logs_within_90s():
    """Run harness for 90s and assert both agents wrote JSONL logs with ≥1 record.

    This is a REAL harness test — it launches SimsVille under Xvfb :99,
    starts the sidecar, and launches two v3 agents. If SimsVille cannot be
    built or Xvfb :99 is not running, this test fails (not skips) because
    FREESIMS_INTEGRATION=1 was explicitly set, indicating the environment
    should be ready.
    """
    # Pre-clean any stale logs from previous runs
    for p in Path("/tmp").glob("embodied-agent-*.jsonl"):
        p.unlink(missing_ok=True)

    env = os.environ.copy()
    env["FREESIMS_AGENT_VERSION"] = "v3"
    env["DISPLAY"] = ":99"

    proc = subprocess.Popen(
        [str(HARNESS), "--sim1-name", "Alice", "--sim2-name", "Bob"],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        preexec_fn=os.setsid,  # create new process group for clean kill
    )

    # Wait up to 90s, then signal shutdown
    deadline = 90
    poll_interval = 5
    elapsed = 0
    while elapsed < deadline:
        time.sleep(poll_interval)
        elapsed += poll_interval
        if proc.poll() is not None:
            break  # harness exited on its own

    # Send SIGTERM to the entire process group to trigger cleanup()
    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
    except ProcessLookupError:
        pass  # already gone

    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        proc.wait(timeout=5)

    stdout = proc.stdout.read().decode(errors="replace") if proc.stdout else ""
    stderr = proc.stderr.read().decode(errors="replace") if proc.stderr else ""

    # Assert: ≥2 embodied-agent log files exist
    agent_logs = list(Path("/tmp").glob("embodied-agent-*.jsonl"))
    assert len(agent_logs) >= 2, (
        f"Expected ≥2 /tmp/embodied-agent-*.jsonl, found {len(agent_logs)}.\n"
        f"stdout={stdout[-2000:]}\nstderr={stderr[-2000:]}"
    )

    # Assert: each log has ≥1 record (perception or turn)
    for log_path in agent_logs:
        lines = [l.strip() for l in log_path.read_text().splitlines() if l.strip()]
        assert len(lines) >= 1, (
            f"{log_path} is empty — agent produced no JSONL records.\n"
            f"stdout={stdout[-1000:]}"
        )
        # Validate JSON parseable
        for line in lines[:5]:
            try:
                json.loads(line)
            except json.JSONDecodeError as e:
                pytest.fail(f"{log_path} contains non-JSON line: {line!r} — {e}")

    # Assert: at least one log has a 'turn' event (≥1 LLM turn)
    # If the Agent SDK is not installed, this may not be present — we fail loudly.
    total_turns = 0
    for log_path in agent_logs:
        for line in log_path.read_text().splitlines():
            if not line.strip():
                continue
            try:
                record = json.loads(line)
                if record.get("event") in ("turn", "text"):
                    total_turns += 1
            except json.JSONDecodeError:
                pass

    assert total_turns >= 1, (
        f"Expected ≥1 LLM turn record across agent logs, found {total_turns}.\n"
        f"This likely means claude-agent-sdk is not installed or claude CLI is missing.\n"
        f"Logs: {[str(p) for p in agent_logs]}\n"
        f"stdout={stdout[-2000:]}\nstderr={stderr[-2000:]}"
    )


@requires_integration
def test_harness_no_orphan_processes_after_cleanup():
    """After harness exits, no orphaned claude --print or sim-agent-v3 python processes remain.

    Runs harness for 30s then kills it, then checks for orphans.
    """
    # Pre-clean
    for p in Path("/tmp").glob("embodied-agent-*.jsonl"):
        p.unlink(missing_ok=True)

    env = os.environ.copy()
    env["FREESIMS_AGENT_VERSION"] = "v3"
    env["DISPLAY"] = ":99"

    proc = subprocess.Popen(
        [str(HARNESS), "--sim1-name", "Test1", "--sim2-name", "Test2"],
        env=env,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        preexec_fn=os.setsid,
    )

    time.sleep(30)

    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGINT)
    except ProcessLookupError:
        pass

    try:
        proc.wait(timeout=20)
    except subprocess.TimeoutExpired:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        proc.wait(timeout=5)

    # Wait a moment for cleanup to fully propagate
    time.sleep(3)

    # Check for orphaned claude --print processes
    result_claude = subprocess.run(
        ["pgrep", "-f", "claude --print"],
        capture_output=True, text=True
    )
    orphaned_claude = result_claude.stdout.strip()

    # Check for orphaned sim-agent-v3 python processes
    result_agent = subprocess.run(
        ["pgrep", "-f", "sim-agent-v3"],
        capture_output=True, text=True
    )
    orphaned_agent = result_agent.stdout.strip()

    assert not orphaned_claude, (
        f"Orphaned 'claude --print' processes found after harness cleanup: PIDs={orphaned_claude}\n"
        "The cleanup() trap must kill Agent SDK subprocesses."
    )
    assert not orphaned_agent, (
        f"Orphaned 'sim-agent-v3' python processes found after harness cleanup: PIDs={orphaned_agent}\n"
        "The cleanup() trap must kill all agent python processes."
    )


@requires_integration
def test_harness_v2_fallback_path():
    """FREESIMS_AGENT_VERSION=v2 should launch sim-agent-v2.py (not v3).

    We verify by checking the agent0 stderr log for v2-specific output.
    v2 uses SIM_AGENT_NAME env var and prints [agent0]-prefixed messages.
    v3 uses SIM_NAME and prints [sim-agent-v3]-prefixed messages.
    """
    env = os.environ.copy()
    env["FREESIMS_AGENT_VERSION"] = "v2"
    env["DISPLAY"] = ":99"

    # Clean up any previous v3 logs to avoid confusion
    for p in Path("/tmp").glob("embodied-agent-*.jsonl"):
        p.unlink(missing_ok=True)

    proc = subprocess.Popen(
        [str(HARNESS), "--sim1-name", "Alice", "--sim2-name", "Bob"],
        env=env,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        preexec_fn=os.setsid,
    )

    time.sleep(20)

    try:
        os.killpg(os.getpgid(proc.pid), signal.SIGTERM)
    except ProcessLookupError:
        pass

    try:
        proc.wait(timeout=15)
    except subprocess.TimeoutExpired:
        os.killpg(os.getpgid(proc.pid), signal.SIGKILL)
        proc.wait()

    stdout = proc.stdout.read().decode(errors="replace") if proc.stdout else ""

    # Verify the harness reported v2 in its output
    assert "v2" in stdout or "sim-agent-v2" in stdout, (
        f"Harness stdout did not confirm v2 agent selection.\nstdout={stdout[:2000]}"
    )
    # v3 log files should NOT have been created
    v3_logs = list(Path("/tmp").glob("embodied-agent-*.jsonl"))
    assert len(v3_logs) == 0, (
        f"v3 embodied-agent logs found when FREESIMS_AGENT_VERSION=v2: {v3_logs}\n"
        "v2 agents do not produce these files — check that agent selection is correct."
    )
