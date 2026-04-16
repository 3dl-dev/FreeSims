#!/usr/bin/env python3
"""
sim-agent-v2.py — Heuristic Sim agent for the multi-agent demo.

Reads perception events (+ response frames) from the FreeSims sidecar on stdin
(line-delimited JSON) and writes interaction commands to stdout.

One agent process per Sim. The demo launcher pipes each agent to a dedicated
sidecar subprocess that filters perceptions to that Sim's persist_id.

Protocol overview
-----------------
Sidecar emits (stdout, JSONL):
  {"type":"perception", "persist_id":N, "name":"...", "funds":F, "clock":{...},
   "motives":{...}, "nearby_objects":[...], "lot_avatars":[...], ...}
  {"type":"response", "request_id":"...", "status":"ok", "payload":{...}}
  {"tick_id":N, "command_count":N, "random_seed":N}   -- tick acks (ignored)
  {"type":"pathfind-failed", ...}
  {"type":"dialog", "dialog_id":N, ...}

Agent writes (stdout, JSONL):
  {"type":"query-catalog", "actor_uid":N, "request_id":"cat-1", "category":"all"}
  {"type":"buy", "actor_uid":N, "guid":G, "x":X, "y":Y, "level":1}
  {"type":"interact", "actor_uid":N, "interaction_id":I, "target_id":T, "param0":0}
  {"type":"chat", "actor_uid":N, "message":"..."}
  {"type":"dialog-response", "dialog_id":D, "button":"Yes"}

Exit after 10 in-game minutes or 2 real minutes, whichever comes first.

Environment variables
---------------------
  SIM_AGENT_NAME   — filter to this Sim name (match against perception.name)
  SIM_AGENT_INDEX  — agent index (0 or 1) for logging disambiguation
"""

import json
import sys
import os
import time
import random
import signal

SIM_NAME = os.environ.get("SIM_AGENT_NAME", "")
AGENT_INDEX = int(os.environ.get("SIM_AGENT_INDEX", "0"))
REAL_TIMEOUT = 150          # 2.5 real-time minutes
INGAME_MINUTES_TARGET = 10  # 10 in-game minutes

TAG = f"[agent{AGENT_INDEX}]"


def log(msg: str):
    print(f"{TAG} {msg}", file=sys.stderr, flush=True)


def send(cmd: dict):
    print(json.dumps(cmd), flush=True)
    log(f"  -> {cmd.get('type')}: {json.dumps(cmd)[:100]}")


# ---------------------------------------------------------------------------
# State machine
# ---------------------------------------------------------------------------

class SimAgent:
    def __init__(self):
        self.persist_id: int | None = None
        self.name: str = ""
        self.funds: int = 0
        self.initial_funds: int | None = None

        # Clock tracking
        self.clock_hours: int | None = None
        self.clock_minutes: int | None = None
        self.ingame_start_minutes: int | None = None  # hours*60+min at first perception
        self.ingame_elapsed_minutes: float = 0.0
        self.last_time_of_day: int | None = None  # for day/night awareness

        # Catalog state
        self.catalog_requested = False
        self.catalog_received = False  # True once a catalog response arrives (even if empty)
        self.catalog_req_id = "cat-1"
        self.catalog_items: list[dict] = []
        self.cheap_items: list[dict] = []
        self.buy_issued = False
        self.bought_guid: int | None = None
        self.ticks_after_buy = 0         # count perceptions after buy; wait >=5 before exiting
        self.buy_time: float | None = None  # real time when buy was issued

        # Observation
        self.lot_avatar_counts_logged: set[int] = set()

        self.real_start = time.monotonic()
        self.tick = 0

    # --- helpers ---

    def real_elapsed(self) -> float:
        return time.monotonic() - self.real_start

    def update_clock(self, clock: dict):
        hours = clock.get("hours", 0)
        minutes = clock.get("minutes", 0)

        if self.ingame_start_minutes is None:
            self.ingame_start_minutes = hours * 60 + minutes
            log(f"start time: {hours:02d}:{minutes:02d}")

        current_total = hours * 60 + minutes
        # Handle day wrap (if the game clock wraps midnight)
        if self.clock_hours is not None:
            prev_total = self.clock_hours * 60 + (self.clock_minutes or 0)
            if current_total < prev_total and prev_total - current_total > 120:
                # Day wrapped — add (1440 - prev + current)
                delta = 1440 - prev_total + current_total
            else:
                delta = max(0, current_total - prev_total)
            self.ingame_elapsed_minutes += delta

        self.clock_hours = hours
        self.clock_minutes = minutes

        # Hour change log
        if self.last_time_of_day is None or hours != self.clock_hours:
            self.last_time_of_day = hours
            log(f"time is now {hours:02d}:{minutes:02d} (in-game elapsed: {self.ingame_elapsed_minutes:.1f}m)")

    def check_night_awareness(self, clock: dict):
        """Log day/night transition for clock-awareness check."""
        tod = clock.get("time_of_day", -1)
        if tod != self.last_time_of_day:
            self.last_time_of_day = tod
            label = {0: "dawn", 1: "morning", 2: "afternoon", 3: "evening", 4: "night"}.get(tod, "unknown")
            log(f"time_of_day changed to {label} ({tod})")

    def choose_buy_position(self, perception: dict) -> tuple[int, int]:
        """Pick a tile to place the bought object.
        Use a fixed offset far from the Sim to avoid placing on an occupied tile.
        Agent index offsets ensure agent0 and agent1 don't collide with each other.
        """
        base_x, base_y = 8 + AGENT_INDEX * 4, 8 + AGENT_INDEX * 4
        return base_x, base_y

    # Hardcoded fallback GUIDs from Content/catalog.xml (WorldCatalog source of truth).
    # These are known-cheap items that VMNetBuyObjectCmd.Verify can find and afford.
    FALLBACK_ITEMS = [
        {"guid": 0x311BD32E, "name": "Lamp - Candle - Large", "price": 1},   # cat 19
        {"guid": 0x3189B483, "name": "Lamp - Candle - Black", "price": 1},   # cat 19
        {"guid": 0x0066D8A0, "name": "Urn Stone - Pet", "price": 25},        # cat 18
        {"guid": 0x00230EDC, "name": "DollHouse", "price": 180},             # cat 15
    ]

    def pick_cheap_item(self) -> dict | None:
        """Pick the cheapest non-zero-price item.

        Prefers items from the catalog query response; falls back to known-cheap
        items from WorldCatalog (catalog.xml) which VMNetBuyObjectCmd can look up.
        """
        candidates = [i for i in self.catalog_items if 0 < i.get("price", 0) <= 200]
        if not candidates:
            candidates = [i for i in self.catalog_items if i.get("price", 0) > 0]
        if candidates:
            return min(candidates, key=lambda i: i["price"])
        # Fallback: use a known-good WorldCatalog item
        log("no priced items in query-catalog response; using fallback WorldCatalog item")
        return self.FALLBACK_ITEMS[AGENT_INDEX % len(self.FALLBACK_ITEMS)]

    def pick_interaction(self, nearby_objects: list[dict]) -> dict | None:
        """Pick the first available interaction on any nearby object."""
        for obj in nearby_objects:
            if obj.get("interactions"):
                return {
                    "object_id": obj["object_id"],
                    "interaction": obj["interactions"][0],
                }
        return None

    # --- main logic ---

    def on_perception(self, data: dict):
        self.tick += 1

        # Capture identify
        if self.persist_id is None:
            self.persist_id = data["persist_id"]
            self.name = data.get("name", "?")
            log(f"locked onto: {self.name} (persist_id={self.persist_id})")

        self.funds = data.get("funds", 0)
        if self.initial_funds is None:
            self.initial_funds = self.funds
            log(f"initial funds: {self.initial_funds}§")

        clock = data.get("clock", {})
        self.update_clock(clock)
        self.check_night_awareness(clock)

        # Observe lot_avatars
        lot_avatars = data.get("lot_avatars", [])
        count = len(lot_avatars)
        if count > 0 and count not in self.lot_avatar_counts_logged:
            self.lot_avatar_counts_logged.add(count)
            names = [a.get("name", "?") for a in lot_avatars]
            log(f"observed {count} lot_avatar(s): {', '.join(names)}")

        motives = data.get("motives", {})
        log(
            f"[tick {self.tick}] {self.name}: funds={self.funds}§ "
            f"hunger={motives.get('hunger')} energy={motives.get('energy')} "
            f"elapsed={self.ingame_elapsed_minutes:.1f}in-game-min"
        )

        # === Decision tree ===

        # 1. Request catalog if not yet done
        if not self.catalog_requested:
            log("requesting catalog...")
            send({
                "type": "query-catalog",
                "actor_uid": self.persist_id,
                "request_id": self.catalog_req_id,
                "category": "all",
            })
            self.catalog_requested = True
            return

        # 2. Wait for catalog response before acting (if still pending)
        if not self.catalog_received:
            log("waiting for catalog response...")
            return

        # 3. Buy an object once (if we haven't yet)
        # Use cheap_items from catalog, or fall back to known-good WorldCatalog GUIDs.
        # No client-side funds check — let the game engine enforce affordability.
        if not self.buy_issued:
            item = self.cheap_items[0] if self.cheap_items else self.pick_cheap_item()
            if item is not None:
                x, y = self.choose_buy_position(data)
                log(f"buying: {item['name']} (guid={item['guid']}, price={item['price']}§) at ({x},{y})")
                send({
                    "type": "buy",
                    "actor_uid": self.persist_id,
                    "guid": item["guid"],
                    "x": x,
                    "y": y,
                    "level": 1,
                    "dir": 0,
                })
                self.buy_issued = True
                self.bought_guid = item["guid"]
                self.buy_time = time.monotonic()
                return

        # Track ticks after buy so we can capture the post-buy perception (funds update)
        if self.buy_issued:
            self.ticks_after_buy += 1

        # 4. Interact with a nearby object if queue is empty
        nearby = data.get("nearby_objects", [])
        action_queue = data.get("action_queue", [])
        if not action_queue and nearby:
            picked = self.pick_interaction(nearby)
            if picked:
                iact = picked["interaction"]
                log(f"interacting: {iact['name']} on object_id={picked['object_id']}")
                send({
                    "type": "interact",
                    "actor_uid": self.persist_id,
                    "interaction_id": iact["id"],
                    "target_id": picked["object_id"],
                    "param0": 0,
                })
                return

        # 5. Say something every ~30 in-game seconds based on mood
        if self.tick % 8 == 0:
            mood = motives.get("mood", 0)
            if mood < -20:
                msg = f"I'm not feeling great (mood={mood})"
            elif mood > 40:
                msg = f"Life is good! (mood={mood})"
            else:
                msg = f"Just living life. (mood={mood})"
            log(f"chat: {msg}")
            send({
                "type": "chat",
                "actor_uid": self.persist_id,
                "message": msg,
            })

    def on_response(self, data: dict):
        req_id = data.get("request_id", "")
        status = data.get("status", "")
        if req_id == self.catalog_req_id and status == "ok":
            payload = data.get("payload")
            # Payload is the catalog array (may be encoded as a string or list)
            if isinstance(payload, str):
                try:
                    items = json.loads(payload)
                except json.JSONDecodeError:
                    items = []
            elif isinstance(payload, list):
                items = payload
            else:
                items = []
            self.catalog_received = True
            self.catalog_items = items
            self.cheap_items = sorted(
                [i for i in items if 0 < i.get("price", 0) <= 200],
                key=lambda i: i["price"]
            )
            log(
                f"catalog received: {len(items)} items total, "
                f"{len(self.cheap_items)} under 200§"
            )
            if self.cheap_items:
                best = self.cheap_items[0]
                log(f"cheapest: {best['name']} @ {best['price']}§ (guid={best['guid']})")

    def on_dialog(self, data: dict):
        """Auto-dismiss dialogs by clicking the first button (or 'Yes' if available)."""
        dialog_id = data.get("dialog_id")
        buttons = data.get("buttons", [])
        title = data.get("title", "")
        log(f"dialog: id={dialog_id} title='{title}' buttons={buttons}")
        if dialog_id:
            btn = "Yes" if "Yes" in buttons else (buttons[0] if buttons else "")
            if btn:
                send({
                    "type": "dialog-response",
                    "dialog_id": dialog_id,
                    "button": btn,
                })

    def should_exit(self) -> bool:
        if self.real_elapsed() >= REAL_TIMEOUT:
            log(f"real-time timeout ({REAL_TIMEOUT}s)")
            return True
        # Don't exit on ingame minutes until we've had >=5 perceptions after the buy
        # (to capture the post-buy funds update in the summary)
        if self.ingame_elapsed_minutes >= INGAME_MINUTES_TARGET:
            # After buying, wait >=5 ticks AND >=15 real seconds to capture post-buy perception
            buy_settled = (
                not self.buy_issued or
                (self.ticks_after_buy >= 5 and
                 self.buy_time is not None and
                 (time.monotonic() - self.buy_time) >= 15.0)
            )
            if buy_settled:
                log(f"in-game target reached ({self.ingame_elapsed_minutes:.1f}m >= {INGAME_MINUTES_TARGET}m)")
                return True
        return False

    def summary(self):
        """Log a summary when exiting."""
        funds_delta = (self.funds - self.initial_funds) if self.initial_funds is not None else 0
        log("=== SUMMARY ===")
        log(f"  Sim: {self.name} (persist_id={self.persist_id})")
        log(f"  Initial funds: {self.initial_funds}§  Final funds: {self.funds}§  Delta: {funds_delta}§")
        log(f"  Bought: {'yes (guid=' + str(self.bought_guid) + ')' if self.buy_issued else 'no'}")
        log(f"  In-game elapsed: {self.ingame_elapsed_minutes:.1f} minutes")
        log(f"  Real elapsed: {self.real_elapsed():.1f}s")
        log(f"  Lot avatars observed: {sorted(self.lot_avatar_counts_logged)}")
        log(f"  Ticks: {self.tick}")


def main():
    agent = SimAgent()

    # Write summary on SIGTERM (demo script kills with SIGTERM when timeout fires)
    def _sigterm_handler(signum, frame):
        agent.summary()
        sys.exit(0)
    signal.signal(signal.SIGTERM, _sigterm_handler)

    log(f"started (SIM_NAME={SIM_NAME!r})")
    log("waiting for perception events on stdin...")

    for raw_line in sys.stdin:
        line = raw_line.strip()
        if not line or not line.startswith("{"):
            continue

        try:
            data = json.loads(line)
        except json.JSONDecodeError:
            continue

        msg_type = data.get("type")

        if msg_type == "perception":
            # Filter to our Sim if SIM_AGENT_NAME is set
            if SIM_NAME and data.get("name") != SIM_NAME:
                continue
            # Skip if we've locked onto a different Sim
            if agent.persist_id is not None and data.get("persist_id") != agent.persist_id:
                continue
            agent.on_perception(data)

        elif msg_type == "response":
            agent.on_response(data)

        elif msg_type == "dialog":
            # Only handle dialogs for our Sim
            if agent.persist_id is not None and data.get("sim_persist_id") != agent.persist_id:
                continue
            agent.on_dialog(data)

        elif msg_type == "pathfind-failed":
            if agent.persist_id is not None and data.get("sim_persist_id") == agent.persist_id:
                log(f"pathfind-failed: reason={data.get('reason')}")

        if agent.should_exit():
            break

    agent.summary()


if __name__ == "__main__":
    main()
