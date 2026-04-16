/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// freesims-sidecar bridges external agents (via campfire or stdin JSON) to
// the FreeSims game process over the VMIPCDriver Unix domain socket.
//
// Usage:
//
//	echo '{"type":"chat","actor_uid":1,"message":"Hello"}' | freesims-sidecar --sock /tmp/freesims-ipc.sock
//
// The sidecar reads JSON commands from stdin, serialises them as VMNetCommand
// binary frames, and sends them over the IPC socket. Tick acks from the game
// are printed to stdout as JSON.
package main

import (
	"bufio"
	"encoding/json"
	"flag"
	"fmt"
	"log"
	"os"
	"os/signal"
	"syscall"

	"github.com/3dl-dev/freesims-sidecar/ipc"
)

// jsonCommand is the JSON schema for commands read from stdin.
type jsonCommand struct {
	Type     string `json:"type"`
	ActorUID uint32 `json:"actor_uid"`

	// Optional correlation ID. When set, the sidecar will receive a response frame
	// from the game engine on ResponseCh and print it to stdout as JSONL with
	// type=response, request_id=<this value>, status=ok|error.
	RequestID string `json:"request_id,omitempty"`

	// Chat fields
	Message string `json:"message,omitempty"`

	// Interaction fields
	InteractionID uint16 `json:"interaction_id,omitempty"`
	TargetID      int16  `json:"target_id,omitempty"`
	Param0        int16  `json:"param0,omitempty"`
	Preempt       bool   `json:"preempt,omitempty"`

	// Goto / Buy / Move fields
	X     int16 `json:"x,omitempty"`
	Y     int16 `json:"y,omitempty"`
	Level int8  `json:"level,omitempty"`

	// Buy fields
	GUID uint32 `json:"guid,omitempty"`
	Dir  byte   `json:"dir,omitempty"`

	// Move / Delete fields
	ObjectID   int16 `json:"object_id,omitempty"`
	CleanupAll bool  `json:"cleanup_all,omitempty"`

	// Lot-size fields
	Size    byte `json:"size,omitempty"`
	Stories byte `json:"stories,omitempty"`

	// Architecture fields
	Ops []jsonArchOp `json:"ops,omitempty"`

	// QueryCatalog fields
	Category string `json:"category,omitempty"`

	// LoadLot fields
	HouseXml string `json:"house_xml,omitempty"`
}

// jsonArchOp mirrors ipc.ArchOp for JSON decoding.
type jsonArchOp struct {
	Kind    string `json:"kind"`
	X       int32  `json:"x"`
	Y       int32  `json:"y"`
	Level   int8   `json:"level"`
	X2      int32  `json:"x2,omitempty"`
	Y2      int32  `json:"y2,omitempty"`
	Pattern uint16 `json:"pattern,omitempty"`
	Style   uint16 `json:"style,omitempty"`
}

// archKindLookup maps string kind names to the ArchCommandType enum.
var archKindLookup = map[string]ipc.ArchCommandType{
	"wall_line":    ipc.ArchWallLine,
	"wall_delete":  ipc.ArchWallDelete,
	"wall_rect":    ipc.ArchWallRect,
	"pattern_dot":  ipc.ArchPatternDot,
	"pattern_fill": ipc.ArchPatternFill,
	"floor_rect":   ipc.ArchFloorRect,
	"floor_fill":   ipc.ArchFloorFill,
	// Aliases for convenience (schema example uses "place_wall").
	"place_wall":  ipc.ArchWallLine,
	"place_floor": ipc.ArchFloorRect,
}

func main() {
	sockPath := flag.String("sock", "/tmp/freesims-ipc.sock", "path to VMIPCDriver Unix socket")
	campfire := flag.Bool("campfire", false, "enable campfire convention publishing (requires CampfireSDK integration)")
	flag.Parse()

	log.SetOutput(os.Stderr)
	log.SetFlags(log.Ltime | log.Lmicroseconds)

	if *campfire {
		log.Println("[sidecar] --campfire: convention declarations are embedded (6 operations)")
		log.Println("[sidecar] --campfire: CampfireSDK integration not yet wired — declarations will be published when SDK is available")
	}

	client := ipc.NewClient(*sockPath)

	// Handle signals for clean shutdown
	sigCh := make(chan os.Signal, 1)
	signal.Notify(sigCh, syscall.SIGINT, syscall.SIGTERM)
	go func() {
		<-sigCh
		log.Println("[sidecar] shutting down")
		client.Close()
		os.Exit(0)
	}()

	// Print tick acks to stdout as JSON
	go func() {
		for ack := range client.AckCh {
			out, _ := json.Marshal(map[string]interface{}{
				"tick_id":       ack.TickID,
				"command_count": ack.CommandCount,
				"random_seed":   ack.RandomSeed,
			})
			fmt.Println(string(out))
		}
	}()

	// Print perception events to stdout as JSONL (one line per event)
	go func() {
		for p := range client.PerceptionCh {
			fmt.Println(string(p))
		}
	}()

	// Print response frames (command correlation results) to stdout as JSONL
	go func() {
		for rf := range client.ResponseCh {
			out, _ := json.Marshal(rf)
			fmt.Println(string(out))
		}
	}()

	// Print pathfind-failed events to stdout as JSONL (reeims-9e7)
	go func() {
		for pf := range client.PathfindFailedCh {
			out, _ := json.Marshal(pf)
			fmt.Println(string(out))
		}
	}()

	// Connect (blocks until connected or closed)
	go func() {
		if err := client.Connect(); err != nil {
			log.Printf("[sidecar] connect failed: %v", err)
		}
	}()

	// Read JSON commands from stdin
	scanner := bufio.NewScanner(os.Stdin)
	for scanner.Scan() {
		line := scanner.Bytes()
		if len(line) == 0 {
			continue
		}

		var jcmd jsonCommand
		if err := json.Unmarshal(line, &jcmd); err != nil {
			log.Printf("[sidecar] invalid JSON: %v", err)
			continue
		}

		cmd, err := parseCommand(jcmd)
		if err != nil {
			log.Printf("[sidecar] %v", err)
			continue
		}

		payload, err := ipc.SerializeCommand(cmd)
		if err != nil {
			log.Printf("[sidecar] serialize error: %v", err)
			continue
		}

		if err := client.SendFrame(payload); err != nil {
			log.Printf("[sidecar] send error: %v", err)
			continue
		}

		log.Printf("[sidecar] sent %s command (actor=%d)", jcmd.Type, jcmd.ActorUID)
	}

	if err := scanner.Err(); err != nil {
		log.Printf("[sidecar] stdin read error: %v", err)
	}

	client.Close()
}

func parseCommand(jcmd jsonCommand) (ipc.Command, error) {
	switch jcmd.Type {
	case "chat":
		if jcmd.Message == "" {
			return nil, fmt.Errorf("chat command requires non-empty 'message'")
		}
		return &ipc.ChatCmd{
			ActorUID:  jcmd.ActorUID,
			Message:   jcmd.Message,
			RequestID: jcmd.RequestID,
		}, nil

	case "interact":
		var preempt byte
		if jcmd.Preempt {
			preempt = 1
		}
		return &ipc.InteractionCmd{
			ActorUID:    jcmd.ActorUID,
			Interaction: jcmd.InteractionID,
			CalleeID:    jcmd.TargetID,
			Param0:      jcmd.Param0,
			Preempt:     preempt,
			RequestID:   jcmd.RequestID,
		}, nil

	case "goto":
		level := jcmd.Level
		if level == 0 {
			level = 1
		}
		return &ipc.GotoCmd{
			ActorUID:    jcmd.ActorUID,
			Interaction: 0, // default interaction for goto marker
			X:           jcmd.X,
			Y:           jcmd.Y,
			Level:       level,
		}, nil

	case "buy":
		if jcmd.GUID == 0 {
			return nil, fmt.Errorf("buy command requires non-zero 'guid'")
		}
		level := jcmd.Level
		if level == 0 {
			level = 1
		}
		return &ipc.BuyObjectCmd{
			ActorUID: jcmd.ActorUID,
			GUID:     jcmd.GUID,
			X:        jcmd.X,
			Y:        jcmd.Y,
			Level:    level,
			Dir:      jcmd.Dir,
		}, nil

	case "move":
		if jcmd.ObjectID == 0 {
			return nil, fmt.Errorf("move command requires non-zero 'object_id'")
		}
		level := jcmd.Level
		if level == 0 {
			level = 1
		}
		return &ipc.MoveObjectCmd{
			ActorUID: jcmd.ActorUID,
			ObjectID: jcmd.ObjectID,
			X:        jcmd.X,
			Y:        jcmd.Y,
			Level:    level,
			Dir:      jcmd.Dir,
		}, nil

	case "delete":
		if jcmd.ObjectID == 0 {
			return nil, fmt.Errorf("delete command requires non-zero 'object_id'")
		}
		return &ipc.DeleteObjectCmd{
			ActorUID:   jcmd.ActorUID,
			ObjectID:   jcmd.ObjectID,
			CleanupAll: jcmd.CleanupAll,
		}, nil

	case "lot-size":
		if jcmd.Size == 0 {
			return nil, fmt.Errorf("lot-size command requires non-zero 'size'")
		}
		return &ipc.ChangeLotSizeCmd{
			ActorUID:   jcmd.ActorUID,
			LotSize:    jcmd.Size,
			LotStories: jcmd.Stories,
		}, nil

	case "architecture":
		if len(jcmd.Ops) == 0 {
			return nil, fmt.Errorf("architecture command requires non-empty 'ops'")
		}
		ops := make([]ipc.ArchOp, 0, len(jcmd.Ops))
		for i, jop := range jcmd.Ops {
			kind, ok := archKindLookup[jop.Kind]
			if !ok {
				return nil, fmt.Errorf("architecture op[%d]: unknown kind %q", i, jop.Kind)
			}
			level := jop.Level
			if level == 0 {
				level = 1
			}
			ops = append(ops, ipc.ArchOp{
				Type:    kind,
				X:       jop.X,
				Y:       jop.Y,
				Level:   level,
				X2:      jop.X2,
				Y2:      jop.Y2,
				Pattern: jop.Pattern,
				Style:   jop.Style,
			})
		}
		return &ipc.ArchitectureCmd{
			ActorUID: jcmd.ActorUID,
			Ops:      ops,
		}, nil

	case "query-catalog":
		cat := jcmd.Category
		if cat == "" {
			cat = "all"
		}
		return &ipc.QueryCatalogCmd{
			ActorUID:  jcmd.ActorUID,
			Category:  cat,
			RequestID: jcmd.RequestID,
		}, nil

	case "load-lot":
		if jcmd.HouseXml == "" {
			return nil, fmt.Errorf("load-lot command requires non-empty 'house_xml'")
		}
		return &ipc.LoadLotCmd{
			ActorUID:  jcmd.ActorUID,
			HouseXml:  jcmd.HouseXml,
			RequestID: jcmd.RequestID,
		}, nil

	default:
		return nil, fmt.Errorf("unknown command type: %q", jcmd.Type)
	}
}
