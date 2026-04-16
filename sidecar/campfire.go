/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package main

// Campfire integration: sidecar as a convention server.
//
// At startup (when --campfire is set) we:
//   1. Initialize a protocol.Client (identity from ~/.cf or $CF_HOME).
//   2. Create (or resume, via FREESIMS_CF_LOT) the `freesims.lot` campfire.
//   3. Parse each embedded conventions/*.json into a Declaration.
//   4. Publish declarations to the lot with tags [convention:operation, freesims:<op>].
//   5. Start a convention.Server per action operation (walk-to, speak, interact-with,
//      sim-action, sim-build) — each registers a HandlerFunc that translates the
//      parsed args into an ipc.Command and calls the existing ipc.Client.SendFrame.
//   6. Bridge PerceptionCh into campfire broadcasts on the lot, tagged
//      `freesims:perception` and `sim:<persist_id>`, so each agent's cf read
//      filter picks up only its own Sim's perceptions.
//
// Query conventions (query-sim-state, query-lot-state, query-pie-menu,
// query-catalog) will join next; they require a request/response path through
// the VMIPCDriver correlation channel, not covered in this minimum viable cut.

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strconv"
	"strings"

	"github.com/campfire-net/campfire/pkg/convention"
	"github.com/campfire-net/campfire/pkg/protocol"

	"github.com/3dl-dev/freesims-sidecar/ipc"
)

// StartConventionServer brings the sidecar up as a convention-speaking campfire
// member. It returns the lot campfire ID on success. Blocks are started as
// goroutines; callers continue running the ipc/game loop.
//
// The returned cancel func stops all running convention.Server loops.
func StartConventionServer(ctx context.Context, ipcClient *ipc.Client) (lotID string, cancel context.CancelFunc, err error) {
	cfHome := os.Getenv("CF_HOME")
	if cfHome == "" {
		home, herr := os.UserHomeDir()
		if herr != nil {
			return "", nil, fmt.Errorf("resolve home: %w", herr)
		}
		cfHome = filepath.Join(home, ".cf")
	}

	client, _, err := protocol.Init(cfHome)
	if err != nil {
		return "", nil, fmt.Errorf("protocol.Init(%s): %w", cfHome, err)
	}

	// Resume or create the lot campfire. Filesystem transport keeps the
	// store under <cfHome>/campfires, which persists across sidecar restarts.
	lotID = os.Getenv("FREESIMS_CF_LOT")
	if lotID == "" {
		transportDir := filepath.Join(cfHome, "campfires")
		if err := os.MkdirAll(transportDir, 0o755); err != nil {
			return "", nil, fmt.Errorf("mkdir transport dir: %w", err)
		}
		res, cerr := client.Create(protocol.CreateRequest{
			Description:  "freesims.lot",
			JoinProtocol: "open",
			Transport:    protocol.FilesystemTransport{Dir: transportDir},
		})
		if cerr != nil {
			return "", nil, fmt.Errorf("create freesims.lot: %w", cerr)
		}
		lotID = res.CampfireID
		log.Printf("[campfire] created freesims.lot: %s", lotID)
	} else {
		log.Printf("[campfire] reusing freesims.lot: %s", lotID)
	}

	// Parse embedded declarations.
	decls, err := loadDeclarations()
	if err != nil {
		return "", nil, fmt.Errorf("load declarations: %w", err)
	}
	log.Printf("[campfire] loaded %d convention declarations", len(decls))

	// Publish declarations to the lot so agents can call `help` / read the API.
	for _, decl := range decls {
		data, merr := json.Marshal(decl)
		if merr != nil {
			log.Printf("[campfire] marshal decl %s: %v", decl.Operation, merr)
			continue
		}
		_, serr := client.Send(protocol.SendRequest{
			CampfireID: lotID,
			Payload:    data,
			Tags: []string{
				"convention:operation",
				"freesims:" + decl.Operation,
			},
		})
		if serr != nil {
			log.Printf("[campfire] publish decl %s: %v", decl.Operation, serr)
			continue
		}
	}

	// Serve handlers for operations we know how to translate to IPC.
	srvCtx, srvCancel := context.WithCancel(ctx)

	handlers := map[string]convention.HandlerFunc{
		"walk-to":       walkToHandler(ipcClient),
		"speak":         speakHandler(ipcClient),
		"interact-with": interactWithHandler(ipcClient),
	}

	for _, decl := range decls {
		fn, ok := handlers[decl.Operation]
		if !ok {
			// Query conventions and sim-action/sim-build are declared but not
			// served yet — future work.
			continue
		}
		srv := convention.NewServer(client, decl).
			WithErrorHandler(func(err error) {
				log.Printf("[campfire] handler %s: %v", decl.Operation, err)
			})
		srv.RegisterHandler(decl.Operation, fn)

		go func(op string, s *convention.Server) {
			log.Printf("[campfire] serving %s on freesims.lot", op)
			if serr := s.Serve(srvCtx, lotID); serr != nil && !errors.Is(serr, context.Canceled) {
				log.Printf("[campfire] server %s exited: %v", op, serr)
			}
		}(decl.Operation, srv)
	}

	// Bridge perception frames into tagged campfire broadcasts.
	go perceptionBridge(srvCtx, client, lotID, ipcClient)

	return lotID, srvCancel, nil
}

// loadDeclarations reads every conventions/*.json and parses each into a
// convention.Declaration. Order is stable (sorted by filename).
func loadDeclarations() ([]*convention.Declaration, error) {
	entries, err := conventionFiles.ReadDir("conventions")
	if err != nil {
		return nil, fmt.Errorf("read embedded conventions: %w", err)
	}

	var decls []*convention.Declaration
	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".json" {
			continue
		}
		data, rerr := conventionFiles.ReadFile("conventions/" + entry.Name())
		if rerr != nil {
			return nil, fmt.Errorf("read %s: %w", entry.Name(), rerr)
		}
		var decl convention.Declaration
		if jerr := json.Unmarshal(data, &decl); jerr != nil {
			return nil, fmt.Errorf("parse %s: %w", entry.Name(), jerr)
		}
		decls = append(decls, &decl)
	}
	return decls, nil
}

// walkToHandler returns a convention.HandlerFunc that translates walk-to
// invocations into ipc.GotoCmd frames.
func walkToHandler(ipcClient *ipc.Client) convention.HandlerFunc {
	return func(ctx context.Context, req *convention.Request) (*convention.Response, error) {
		simID, ok := argInt(req.Args, "sim_id")
		if !ok {
			return errResp("missing sim_id"), nil
		}
		level, _ := argInt(req.Args, "level")
		if level == 0 {
			level = 1
		}

		var x, y int64
		if oid, okO := argInt(req.Args, "target_object_id"); okO && oid != 0 {
			// Resolve object_id → position via latest perception (not implemented
			// in this cut — agent is expected to pass x/y from nearby_objects).
			_ = oid
			return errResp("target_object_id resolution not yet implemented; pass x/y instead"), nil
		}
		x, okX := argInt(req.Args, "x")
		y2, okY := argInt(req.Args, "y")
		if !okX || !okY {
			return errResp("walk-to requires x and y (or target_object_id once implemented)"), nil
		}
		y = y2

		cmd := &ipc.GotoCmd{
			ActorUID:    uint32(simID),
			Interaction: 2, // "Walk Here" on the goto marker
			X:           int16(x),
			Y:           int16(y),
			Level:       int8(level),
		}
		if err := sendIPC(ipcClient, cmd); err != nil {
			return errResp(err.Error()), nil
		}
		return okResp(map[string]any{
			"sim_id": simID, "x": x, "y": y, "level": level,
		}), nil
	}
}

// speakHandler translates speak invocations into ipc.ChatCmd frames.
func speakHandler(ipcClient *ipc.Client) convention.HandlerFunc {
	return func(ctx context.Context, req *convention.Request) (*convention.Response, error) {
		simID, ok := argInt(req.Args, "sim_id")
		if !ok {
			return errResp("missing sim_id"), nil
		}
		text, _ := req.Args["text"].(string)
		if strings.TrimSpace(text) == "" {
			return errResp("text is empty"), nil
		}

		cmd := &ipc.ChatCmd{
			ActorUID: uint32(simID),
			Message:  text,
		}
		if err := sendIPC(ipcClient, cmd); err != nil {
			return errResp(err.Error()), nil
		}
		return okResp(map[string]any{
			"sim_id": simID, "text": text,
		}), nil
	}
}

// interactWithHandler translates interact-with invocations into ipc.InteractionCmd.
func interactWithHandler(ipcClient *ipc.Client) convention.HandlerFunc {
	return func(ctx context.Context, req *convention.Request) (*convention.Response, error) {
		simID, ok := argInt(req.Args, "sim_id")
		if !ok {
			return errResp("missing sim_id"), nil
		}
		objID, ok := argInt(req.Args, "object_id")
		if !ok {
			return errResp("missing object_id"), nil
		}
		interactionID, ok := argInt(req.Args, "interaction_id")
		if !ok {
			return errResp("missing interaction_id"), nil
		}

		cmd := &ipc.InteractionCmd{
			ActorUID:    uint32(simID),
			Interaction: uint16(interactionID),
			CalleeID:    int16(objID),
		}
		if err := sendIPC(ipcClient, cmd); err != nil {
			return errResp(err.Error()), nil
		}
		return okResp(map[string]any{
			"sim_id": simID, "object_id": objID, "interaction_id": interactionID,
		}), nil
	}
}

// perceptionBridge forwards every perception frame from the game onto the lot
// campfire, tagged `freesims:perception` plus `sim:<persist_id>`. Agents read
// the lot filtered by their own `sim:<id>` tag and see only their own Sim's
// perceptions.
func perceptionBridge(ctx context.Context, client *protocol.Client, lotID string, ipcClient *ipc.Client) {
	for {
		select {
		case <-ctx.Done():
			return
		case payload, ok := <-ipcClient.PerceptionCh:
			if !ok {
				return
			}
			var shape struct {
				PersistID uint64 `json:"persist_id"`
				SimID     uint64 `json:"sim_id"`
			}
			_ = json.Unmarshal(payload, &shape)
			pid := shape.PersistID
			if pid == 0 {
				pid = shape.SimID
			}
			tags := []string{"freesims:perception"}
			if pid != 0 {
				tags = append(tags, "sim:"+strconv.FormatUint(pid, 10))
			}
			if _, serr := client.Send(protocol.SendRequest{
				CampfireID: lotID,
				Payload:    payload,
				Tags:       tags,
			}); serr != nil {
				log.Printf("[campfire] perception broadcast: %v", serr)
			}
		}
	}
}

// --- helpers ---

func argInt(args map[string]any, key string) (int64, bool) {
	v, ok := args[key]
	if !ok {
		return 0, false
	}
	switch n := v.(type) {
	case float64:
		return int64(n), true
	case int:
		return int64(n), true
	case int64:
		return n, true
	case json.Number:
		i, err := n.Int64()
		return i, err == nil
	case string:
		i, err := strconv.ParseInt(n, 10, 64)
		return i, err == nil
	}
	return 0, false
}

func okResp(payload any) *convention.Response {
	return &convention.Response{
		Payload: map[string]any{"success": true, "result": payload},
	}
}

func errResp(msg string) *convention.Response {
	return &convention.Response{
		Payload: map[string]any{"success": false, "error": msg},
	}
}

// sendIPC marshals the command to its binary frame and writes through the IPC
// client. Returns a user-meaningful error string on failure.
func sendIPC(c *ipc.Client, cmd ipc.Command) error {
	frame, ferr := ipc.SerializeCommand(cmd)
	if ferr != nil {
		return fmt.Errorf("frame: %w", ferr)
	}
	if serr := c.SendFrame(frame); serr != nil {
		return fmt.Errorf("ipc send: %w", serr)
	}
	return nil
}
