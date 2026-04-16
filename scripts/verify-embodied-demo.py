#!/usr/bin/env python3
"""verify-embodied-demo.py — run 7 automated assertions on embodied-agent JSONL logs.

Usage:
  verify-embodied-demo.py <agent-log>... [--strict] [--json]

Each log is /tmp/embodied-agent-<pid>.jsonl written by sim-agent-v3.py.
Exits 0 if all assertions pass (or only non-strict warnings), 1 if any red.
--strict: assertion 5 (identity/third-person) is a hard fail instead of warn.
--json: print machine-parseable JSON to stderr in addition to human output.
"""

import argparse
import json
import re
import sys
from collections import defaultdict
from typing import Any


# ---------------------------------------------------------------------------
# Log loading
# ---------------------------------------------------------------------------

def load_log(path: str) -> list[dict]:
    """Load JSONL from path. Returns list of event dicts."""
    events = []
    with open(path, "r", encoding="utf-8") as f:
        for lineno, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError as exc:
                raise ValueError(f"{path}:{lineno}: invalid JSON: {exc}") from exc
    if not events:
        raise ValueError(f"{path}: log is empty")
    return events


def agent_name_from_events(events: list[dict]) -> str:
    """Heuristic: look for a 'name' field in turn events or ipc_out chat cmds."""
    for ev in events:
        if ev.get("event") == "ipc_out":
            cmd = ev.get("cmd", {})
            if cmd.get("type") == "chat":
                actor = cmd.get("actor_name") or cmd.get("actor")
                if actor:
                    return str(actor)
    # Fallback: filename slug
    return "unknown"


# ---------------------------------------------------------------------------
# Individual assertions — each returns (name, passed, detail)
# ---------------------------------------------------------------------------

def assert_min_turns(events: list[dict], min_turns: int = 5) -> tuple[str, bool, str]:
    """A1: Each agent made >= 5 LLM turns (count event:turn records)."""
    count = sum(1 for ev in events if ev.get("event") == "turn")
    passed = count >= min_turns
    detail = f"{count} turn(s) found (need ≥{min_turns})"
    return "A1:min_turns", passed, detail


def assert_distinct_tools(events: list[dict], min_tools: int = 2) -> tuple[str, bool, str]:
    """A2: Each agent used >= 2 distinct tools."""
    tools: set[str] = set()
    for ev in events:
        if ev.get("event") == "turn":
            tool = ev.get("tool")
            if tool:
                tools.add(str(tool))
    passed = len(tools) >= min_tools
    detail = f"distinct tools used: {sorted(tools) or '[]'} (need ≥{min_tools})"
    return "A2:distinct_tools", passed, detail


def assert_conversation_exchange(
    all_logs: dict[str, list[dict]]
) -> tuple[str, bool, str]:
    """A3: At least one exchange: agent A say -> agent B say within 5 turns.

    Strategy:
    1. If events have 'ts' timestamps, use wall-clock ordering across agents.
       Find any A-say followed by a B-say (different agent) within 5 further
       cross-agent events.
    2. Without timestamps, check that each agent has at least one say event and
       that at least two distinct agents spoke (sufficient for a demo exchange).
    """
    if len(all_logs) < 2:
        return (
            "A3:conversation_exchange",
            False,
            "only one agent log provided — cannot check cross-agent conversation",
        )

    # Collect all say events across agents as (ts_str_or_None, agent_path, message, event_position)
    all_says: list[tuple[str | None, str, str, int]] = []
    has_timestamps = False

    for agent, events in all_logs.items():
        pos = 0
        for ev in events:
            if ev.get("event") == "ipc_out":
                cmd = ev.get("cmd", {})
                if cmd.get("type") == "chat":
                    ts = ev.get("ts")
                    if ts:
                        has_timestamps = True
                    all_says.append((ts, agent, str(cmd.get("message", "")), pos))
            pos += 1

    if not all_says:
        return (
            "A3:conversation_exchange",
            False,
            "no say events found in any agent log",
        )

    # Check that at least two distinct agents spoke
    speakers = {agent for _, agent, _, _ in all_says}
    if len(speakers) < 2:
        return (
            "A3:conversation_exchange",
            False,
            f"only one agent has say events; need both to exchange",
        )

    # If we have timestamps: sort all says by ts, check for A-say then B-say within 5 events
    if has_timestamps:
        sorted_says = sorted(all_says, key=lambda x: x[0] or "")
        for i, (ts_a, agent_a, text_a, _) in enumerate(sorted_says):
            # Look at next 5 say events for a different speaker
            for j in range(i + 1, min(i + 6, len(sorted_says))):
                ts_b, agent_b, text_b, _ = sorted_says[j]
                if agent_b != agent_a:
                    detail = (
                        f"{agent_a!r} said at {ts_a!r} then {agent_b!r} replied"
                        f" at {ts_b!r} (within {j - i} say-events)"
                    )
                    return "A3:conversation_exchange", True, detail
        return (
            "A3:conversation_exchange",
            False,
            "no cross-agent say exchange found within 5 say-events (timestamp ordering)",
        )

    # No timestamps: both agents spoke — count as a conversation for demo purposes.
    # This is a minimal check: we can't order events without timestamps.
    agent_list = sorted(speakers)
    detail = (
        f"both agents have say events: {agent_list} — exchange inferred"
        f" (no timestamps available for strict ordering)"
    )
    return "A3:conversation_exchange", True, detail


def assert_perception_hygiene(
    events: list[dict], self_name: str
) -> tuple[str, bool, str]:
    """A4: No <perception> block contains numeric motives for OTHER Sims.

    We look in turn events for any text content that includes patterns like
    "motives": {...} nested under a name that is NOT self_name.

    We also scan ipc_in frames with perception payloads from lot_avatars.
    """
    findings: list[str] = []

    # Pattern: look for "motives" value that is a non-empty object with numeric values
    # inside something that looks like a non-self Sim entry.
    # Strategy: scan raw text of any "perception" or "ipc_in" events for
    # {"name": "<other>", ..., "motives": {"hunger": <N>}}
    motive_keys = ("hunger", "energy", "comfort", "hygiene", "bladder", "social", "fun", "mood")
    motive_pattern = re.compile(
        r'"(' + "|".join(motive_keys) + r')"\s*:\s*(-?\d+(?:\.\d+)?)'
    )
    name_pattern = re.compile(r'"name"\s*:\s*"([^"]+)"')

    for ev in events:
        if ev.get("event") not in ("perception", "ipc_in", "turn"):
            continue

        # Serialize the event to JSON text so we can do text search
        try:
            raw = json.dumps(ev)
        except (TypeError, ValueError):
            continue

        # Find all lot_avatar-like blocks: extract name + motives
        # Simple approach: split on name occurrences and check for motives nearby
        lot_avatars: Any = None
        if ev.get("event") == "ipc_in":
            frame = ev.get("frame", {})
            lot_avatars = frame.get("lot_avatars")
        elif ev.get("event") == "perception":
            lot_avatars = ev.get("lot_avatars")

        if isinstance(lot_avatars, list):
            for avatar in lot_avatars:
                avatar_name = avatar.get("name", "")
                if avatar_name.lower() == self_name.lower():
                    continue  # self — allowed
                motives = avatar.get("motives")
                if isinstance(motives, dict):
                    for k, v in motives.items():
                        if isinstance(v, (int, float)) and k in motive_keys:
                            findings.append(
                                f"other-Sim {avatar_name!r} has numeric motive "
                                f"{k}={v} in event:{ev.get('event')}"
                            )

        # Also scan turn event text for embedded perception XML/JSON with motive values
        if ev.get("event") == "turn":
            # Look for any numeric motive data in the text field(s) of turn
            for field in ("reasoning", "text", "content"):
                text_val = ev.get(field, "")
                if not isinstance(text_val, str):
                    continue
                # Find <perception> or perception block mentions
                # Look for patterns like `"hunger": 42` near non-self names
                for match in name_pattern.finditer(text_val):
                    name = match.group(1)
                    if name.lower() == self_name.lower():
                        continue
                    # Check for motive keys near this name occurrence
                    region_start = match.start()
                    region_end = min(len(text_val), region_start + 500)
                    region = text_val[region_start:region_end]
                    for mmatch in motive_pattern.finditer(region):
                        findings.append(
                            f"turn text references {name!r} motive "
                            f"{mmatch.group(1)}={mmatch.group(2)}"
                        )

    if findings:
        return (
            "A4:perception_hygiene",
            False,
            f"god-mode leakage ({len(findings)} finding(s)): " + "; ".join(findings[:3]),
        )
    return "A4:perception_hygiene", True, "no numeric other-Sim motives found"


def assert_identity_first_person(
    events: list[dict], self_name: str, strict: bool = False
) -> tuple[str, bool, str]:
    """A5: No third-person self-reference (e.g. 'Gerry will') in reasoning.

    Count occurrences of '<self_name> will' (case-insensitive) across turn text fields.
    Flag count > 2 as red (strict=hard-fail, non-strict=warn).
    """
    if not self_name or self_name == "unknown":
        return "A5:identity_first_person", True, "self_name unknown — skipped"

    pattern = re.compile(re.escape(self_name) + r"\s+will\b", re.IGNORECASE)
    count = 0
    examples: list[str] = []

    for ev in events:
        if ev.get("event") != "turn":
            continue
        for field in ("reasoning", "text", "content"):
            text_val = ev.get(field, "")
            if not isinstance(text_val, str):
                continue
            matches = pattern.findall(text_val)
            count += len(matches)
            if matches and len(examples) < 2:
                # Grab snippet
                m = pattern.search(text_val)
                if m:
                    snippet = text_val[max(0, m.start() - 20) : m.end() + 20]
                    examples.append(repr(snippet))

    threshold = 2
    red = count > threshold
    detail = f"third-person self-references ('{self_name} will'): {count}"
    if examples:
        detail += f" — e.g. {examples[0]}"
    if red and not strict:
        detail += " [WARN — use --strict to hard-fail]"
    return "A5:identity_first_person", not red, detail


def assert_pathfind_resolved(events: list[dict]) -> tuple[str, bool, str]:
    """A6: Every pathfind-failed event followed by new turn with trigger='pathfind_failed' within 3 turns.

    Scan all events; track positions of pathfind-failed events (either in ipc_in frames
    or as explicit event type). For each, look ahead up to 3 'turn' events.
    """
    # Build flat list with types for easy index-based scanning
    flat: list[tuple[str, dict]] = []
    for ev in events:
        etype = ev.get("event", "")
        # Check for pathfind-failed as explicit event
        if etype == "pathfind-failed" or etype == "pathfind_failed":
            flat.append(("pathfind_failed", ev))
        # Check for ipc_in frames indicating pathfind failure
        elif etype == "ipc_in":
            frame = ev.get("frame", {})
            if frame.get("type") == "pathfind-failed" or frame.get("status") == "pathfind_failed":
                flat.append(("pathfind_failed", ev))
            else:
                flat.append(("ipc_in", ev))
        elif etype == "turn":
            flat.append(("turn", ev))
        else:
            flat.append((etype, ev))

    unresolved: list[int] = []
    for i, (etype, ev) in enumerate(flat):
        if etype != "pathfind_failed":
            continue
        # Look ahead for a turn with trigger='pathfind_failed' within 3 turns
        turns_seen = 0
        resolved = False
        for j in range(i + 1, len(flat)):
            jtype, jev = flat[j]
            if jtype == "turn":
                turns_seen += 1
                trigger = jev.get("trigger", "")
                if "pathfind" in str(trigger).lower():
                    resolved = True
                    break
                if turns_seen >= 3:
                    break
        if not resolved:
            unresolved.append(i)

    if unresolved:
        return (
            "A6:pathfind_resolved",
            False,
            f"{len(unresolved)} unresolved pathfind-failed event(s) (at event indices {unresolved[:5]})",
        )
    return "A6:pathfind_resolved", True, "all pathfind-failed events resolved within 3 turns"


def assert_steady_state(events: list[dict]) -> tuple[str, bool, str]:
    """A7: No tool-call errors in last third of session.

    Scan ipc_in frames with status='error' or event='error' in the last third of events.
    """
    n = len(events)
    if n == 0:
        return "A7:steady_state", False, "no events"

    third = max(1, n // 3)
    last_third = events[n - third :]

    errors: list[str] = []
    for ev in last_third:
        etype = ev.get("event", "")
        if etype == "ipc_in":
            frame = ev.get("frame", {})
            if frame.get("status") == "error" or frame.get("type") == "error":
                msg = frame.get("message") or frame.get("error") or "tool-call error"
                errors.append(str(msg))
        elif etype == "error":
            errors.append(ev.get("message") or "tool-call error")
        elif etype == "turn":
            # Check for error in tool result
            result = ev.get("result", "")
            if isinstance(result, str) and "error" in result.lower():
                errors.append(f"turn result: {result[:80]}")

    if errors:
        return (
            "A7:steady_state",
            False,
            f"{len(errors)} error(s) in last third of session: "
            + "; ".join(errors[:3]),
        )
    return "A7:steady_state", True, f"no tool-call errors in last {third} events"


# ---------------------------------------------------------------------------
# Run all assertions for a single agent log
# ---------------------------------------------------------------------------

def run_assertions(
    path: str,
    events: list[dict],
    self_name: str,
    all_logs: dict[str, list[dict]],
    strict: bool,
) -> list[tuple[str, bool, str]]:
    results = [
        assert_min_turns(events),
        assert_distinct_tools(events),
        assert_conversation_exchange(all_logs),
        assert_perception_hygiene(events, self_name),
        assert_identity_first_person(events, self_name, strict=strict),
        assert_pathfind_resolved(events),
        assert_steady_state(events),
    ]
    return results


# ---------------------------------------------------------------------------
# Output formatting
# ---------------------------------------------------------------------------

PASS = "PASS"
FAIL = "FAIL"
WARN = "WARN"


def format_human(
    path: str,
    self_name: str,
    results: list[tuple[str, bool, str]],
    strict: bool,
) -> str:
    lines = [f"\n=== {path} (agent: {self_name}) ==="]
    for name, passed, detail in results:
        # A5 non-strict: show WARN rather than FAIL
        is_a5 = name.startswith("A5")
        if passed:
            status = PASS
        elif is_a5 and not strict:
            status = WARN
        else:
            status = FAIL
        lines.append(f"  [{status}] {name}: {detail}")
    return "\n".join(lines)


def format_json(
    path: str,
    self_name: str,
    results: list[tuple[str, bool, str]],
    strict: bool,
) -> dict:
    assertions = []
    for name, passed, detail in results:
        is_a5 = name.startswith("A5")
        if passed:
            status = "pass"
        elif is_a5 and not strict:
            status = "warn"
        else:
            status = "fail"
        assertions.append({"name": name, "status": status, "detail": detail})
    return {"log": path, "agent": self_name, "assertions": assertions}


# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------

def main() -> int:
    parser = argparse.ArgumentParser(
        description="Verify embodied-demo agent JSONL logs against 7 assertions."
    )
    parser.add_argument(
        "logs",
        nargs="+",
        metavar="agent-log",
        help="One or more /tmp/embodied-agent-<pid>.jsonl log files",
    )
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Assertion 5 (identity) is a hard fail instead of warn",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        dest="json_out",
        help="Print machine-parseable JSON to stderr",
    )
    args = parser.parse_args()

    # Load all logs first so A3 can be cross-agent
    all_events: dict[str, list[dict]] = {}
    load_errors: list[str] = []
    for path in args.logs:
        try:
            all_events[path] = load_log(path)
        except (OSError, ValueError) as exc:
            load_errors.append(str(exc))

    if load_errors:
        for err in load_errors:
            print(f"ERROR: {err}", file=sys.stderr)
        return 1

    if not all_events:
        print("ERROR: no logs loaded", file=sys.stderr)
        return 1

    # Infer agent names
    agent_names: dict[str, str] = {}
    for path, events in all_events.items():
        agent_names[path] = agent_name_from_events(events)

    # Run assertions per agent
    all_results: dict[str, list[tuple[str, bool, str]]] = {}
    for path, events in all_events.items():
        self_name = agent_names[path]
        results = run_assertions(path, events, self_name, all_events, args.strict)
        all_results[path] = results

    # Compute overall pass/fail
    # A5 non-strict counts as pass for exit code purposes
    any_fail = False
    for path, results in all_results.items():
        for name, passed, detail in results:
            is_a5 = name.startswith("A5")
            if not passed:
                if is_a5 and not args.strict:
                    pass  # warn only
                else:
                    any_fail = True

    # Human output (stdout)
    for path in args.logs:
        if path not in all_results:
            continue
        self_name = agent_names[path]
        print(format_human(path, self_name, all_results[path], args.strict))

    # Summary
    total_assertions = sum(len(r) for r in all_results.values())
    total_pass = sum(
        1 for results in all_results.values() for _, passed, _ in results if passed
    )
    print(
        f"\nSummary: {total_pass}/{total_assertions} assertions passed"
        f" across {len(all_results)} log file(s)."
    )
    print("Result:", "ALL GREEN" if not any_fail else "FAILURES DETECTED")

    # JSON output (stderr)
    if args.json_out:
        json_data = {
            "overall": "pass" if not any_fail else "fail",
            "agents": [
                format_json(path, agent_names[path], all_results[path], args.strict)
                for path in args.logs
                if path in all_results
            ],
        }
        print(json.dumps(json_data, indent=2), file=sys.stderr)

    return 1 if any_fail else 0


if __name__ == "__main__":
    sys.exit(main())
