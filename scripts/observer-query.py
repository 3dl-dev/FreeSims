#!/usr/bin/env python3
"""Query the SimObserver JSONL file (reads a static snapshot, not live file)."""
import json, sys, shutil, tempfile, os

def snapshot(src):
    """Copy observer file to a temp file to avoid reading a live-written file."""
    fd, path = tempfile.mkstemp(suffix='.jsonl')
    os.close(fd)
    shutil.copy2(src, path)
    return path

def load(path):
    records = []
    with open(path) as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                records.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    return records

def cmd_first_sim(args):
    """Print the first sim's persist_id (used as ActorUID in commands)."""
    snap = snapshot(args[0])
    try:
        for r in load(snap):
            if 'persist_id' in r:
                print(r['persist_id'])
                return
    finally:
        os.unlink(snap)

def cmd_position(args):
    """Print latest position of a sim as x,y."""
    obs_file, sim_id = args[0], int(args[1])
    snap = snapshot(obs_file)
    try:
        for r in reversed(load(snap)):
            if r.get('persist_id') == sim_id:
                print(f"{r['position']['x']},{r['position']['y']}")
                return
    finally:
        os.unlink(snap)

def cmd_has_message(args):
    """Check if a sim has a message containing text. Prints 'yes' or 'no'."""
    obs_file, sim_id, text = args[0], int(args[1]), args[2]
    snap = snapshot(obs_file)
    try:
        for r in load(snap):
            if r.get('persist_id') == sim_id and text in r.get('message', ''):
                print('yes')
                return
        print('no')
    finally:
        os.unlink(snap)

def cmd_moved(args):
    """Check if sim moved from (cur_x, cur_y). Prints 'yes' or 'no'."""
    obs_file, sim_id = args[0], int(args[1])
    cur_x, cur_y = int(args[2]), int(args[3])
    snap = snapshot(obs_file)
    try:
        for r in reversed(load(snap)):
            if r.get('persist_id') == sim_id:
                x, y = r['position']['x'], r['position']['y']
                print('yes' if abs(x - cur_x) > 5 or abs(y - cur_y) > 5 else 'no')
                return
        print('no')
    finally:
        os.unlink(snap)

commands = {
    'first-sim': cmd_first_sim,
    'position': cmd_position,
    'has-message': cmd_has_message,
    'moved': cmd_moved,
}

if __name__ == '__main__':
    if len(sys.argv) < 2 or sys.argv[1] not in commands:
        print(f"Usage: {sys.argv[0]} <{'|'.join(commands.keys())}> [args...]", file=sys.stderr)
        sys.exit(1)
    commands[sys.argv[1]](sys.argv[2:])
