/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Package ipc implements a Unix domain socket client for the FreeSims
// VMIPCDriver wire protocol. Frames are length-prefixed:
//
//	[4-byte little-endian payload length][payload bytes]
//
// Outbound payloads are serialised VMNetCommands (see commands.go).
// Inbound payloads are tick acknowledgments:
//
//	[4-byte LE tick_id][4-byte LE command_count][8-byte LE random_seed]
package ipc

import (
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"strings"
	"sync"
	"time"
)

const (
	maxFrameSize    = 1_000_000
	reconnectDelay  = 2 * time.Second
	ackPayloadSize  = 16 // 4 + 4 + 8
)

// TickAck is the per-tick acknowledgment sent by VMIPCDriver.
type TickAck struct {
	TickID       uint32
	CommandCount uint32
	RandomSeed   uint64
}

// ResponseFrame is a correlation response emitted by VMIPCDriver after
// executing a command that carried a RequestID.
type ResponseFrame struct {
	Type      string          `json:"type"`       // always "response"
	RequestID string          `json:"request_id"` // matches the RequestID in the command
	Status    string          `json:"status"`     // "ok" or "error"
	Payload   json.RawMessage `json:"payload"`    // command-specific result (may be {})
}

// Client manages a connection to the VMIPCDriver Unix socket.
type Client struct {
	sockPath string
	conn     net.Conn
	mu       sync.Mutex // guards conn

	// AckCh receives tick acks from the game. Buffered so the reader
	// doesn't block the game loop if the consumer is slow.
	AckCh chan TickAck

	// PerceptionCh receives raw JSON perception events from the game.
	// Each element is the complete JSON payload (not length-prefixed).
	PerceptionCh chan []byte

	// ResponseCh receives response frames for commands that carried a RequestID.
	// Keyed by request_id — consumers receive on this channel and match
	// by inspecting ResponseFrame.RequestID. Buffered to avoid blocking.
	ResponseCh chan ResponseFrame

	// PathfindFailedCh receives pathfind-failed events (reeims-9e7).
	// Dedicated channel so agents can select on it separately from
	// perception events and replan routing without scanning PerceptionCh.
	PathfindFailedCh chan PathfindFailed

	// DialogCh receives dialog events (reeims-9be).
	// Each event carries a dialog_id, title, text, and available buttons.
	// Agents respond by sending a DialogResponseCmd with the matching dialog_id.
	DialogCh chan DialogEvent

	// ChatCh receives chat_received events (reeims-7a6).
	// Each event is emitted by VMIPCDriver when a Sim sends a chat message
	// and another Sim is within earshot (L1 distance ≤10 tiles).
	// Fields: sender_persist_id, sender_name, text, recipient_persist_ids.
	ChatCh chan ChatReceived

	done chan struct{}
}

// ChatReceived is the JSON structure sent by the game when a Sim hears a chat
// message from another Sim (reeims-7a6). Earshot is L1 tile distance ≤10.
//
// sender_persist_id and sender_name identify the Sim that spoke.
// text is the chat message (≤200 chars, same limit as the chat command).
// recipient_persist_ids lists the PersistIDs of all Sims within earshot
// (excluding the sender). Agent B can listen on ChatCh to observe chat events.
type ChatReceived struct {
	Type               string   `json:"type"`                 // always "chat_received"
	SenderPersistID    uint32   `json:"sender_persist_id"`
	SenderName         string   `json:"sender_name"`
	Text               string   `json:"text"`
	RecipientPersistIDs []uint32 `json:"recipient_persist_ids"`
}

// NewClient creates a Client that will connect to the given socket path.
// Call Connect to establish the connection.
func NewClient(sockPath string) *Client {
	return &Client{
		sockPath:         sockPath,
		AckCh:            make(chan TickAck, 64),
		PerceptionCh:     make(chan []byte, 64),
		ResponseCh:       make(chan ResponseFrame, 64),
		PathfindFailedCh: make(chan PathfindFailed, 64),
		DialogCh:         make(chan DialogEvent, 64),
		ChatCh:           make(chan ChatReceived, 64),
		done:             make(chan struct{}),
	}
}

// Connect dials the Unix socket. If the socket isn't available yet it
// retries with exponential back-off until ctx is cancelled (or Close
// is called).
func (c *Client) Connect() error {
	for {
		select {
		case <-c.done:
			return errors.New("client closed")
		default:
		}

		conn, err := net.Dial("unix", c.sockPath)
		if err != nil {
			log.Printf("[ipc] connect %s: %v — retrying in %s", c.sockPath, err, reconnectDelay)
			select {
			case <-time.After(reconnectDelay):
				continue
			case <-c.done:
				return errors.New("client closed")
			}
		}

		c.mu.Lock()
		c.conn = conn
		c.mu.Unlock()

		log.Printf("[ipc] connected to %s", c.sockPath)
		go c.readLoop()
		return nil
	}
}

// SendFrame writes a length-prefixed frame to the socket.
func (c *Client) SendFrame(payload []byte) error {
	if len(payload) > maxFrameSize {
		return fmt.Errorf("frame too large: %d > %d", len(payload), maxFrameSize)
	}

	header := make([]byte, 4)
	binary.LittleEndian.PutUint32(header, uint32(len(payload)))

	c.mu.Lock()
	conn := c.conn
	c.mu.Unlock()

	if conn == nil {
		return errors.New("not connected")
	}

	// Write header + payload atomically (best-effort; Unix stream sockets
	// don't guarantee it, but at these sizes it will go in one write).
	buf := make([]byte, 0, 4+len(payload))
	buf = append(buf, header...)
	buf = append(buf, payload...)

	_, err := conn.Write(buf)
	if err != nil {
		c.handleDisconnect(err)
		return err
	}
	return nil
}

// Close shuts down the client.
func (c *Client) Close() {
	select {
	case <-c.done:
		return
	default:
		close(c.done)
	}

	c.mu.Lock()
	if c.conn != nil {
		c.conn.Close()
		c.conn = nil
	}
	c.mu.Unlock()
}

// readLoop reads frames from the socket and dispatches them to AckCh or
// PerceptionCh based on content. The two frame types share the same
// length-prefixed wire format but differ in payload:
//   - Tick ack: exactly 16 bytes, binary (does not start with '{')
//   - Perception: variable-length JSON (starts with '{')
func (c *Client) readLoop() {
	for {
		select {
		case <-c.done:
			return
		default:
		}

		payload, err := c.readFrame()
		if err != nil {
			c.handleDisconnect(err)
			return
		}

		// Frame discrimination by LENGTH only. Tick acks are always exactly 16 bytes.
		// JSON frames are always > 16 bytes (shortest valid type string "dialog" already
		// puts a minimal frame well past 16 bytes). Do NOT check payload[0] for '{' —
		// tick_id byte[0] can legitimately equal 0x7b (e.g. tick_id 379, 635, 891, ...),
		// which would mis-route the ack to the JSON parser.
		if len(payload) == ackPayloadSize {
			ack := TickAck{
				TickID:       binary.LittleEndian.Uint32(payload[0:4]),
				CommandCount: binary.LittleEndian.Uint32(payload[4:8]),
				RandomSeed:   binary.LittleEndian.Uint64(payload[8:16]),
			}
			select {
			case c.AckCh <- ack:
			default:
				// Drop if channel full — consumer is too slow.
			}
		} else if len(payload) > 0 && payload[0] == '{' {
			// JSON frame — distinguish by "type" field
			c.dispatchJSONFrame(payload)
		} else {
			log.Printf("[ipc] unknown frame: %d bytes, first byte 0x%02x", len(payload), payload[0])
		}
	}
}

// dispatchJSONFrame routes a JSON payload to PerceptionCh or ResponseCh based
// on the "type" field. It does a lightweight string prefix check before full
// unmarshal to avoid allocating for the common perception path.
func (c *Client) dispatchJSONFrame(payload []byte) {
	// Quick check: does the JSON contain "type":"response"?
	// We do a full unmarshal of just the type field to be correct.
	var header struct {
		Type string `json:"type"`
	}
	if err := json.Unmarshal(payload, &header); err != nil {
		log.Printf("[ipc] JSON frame unmarshal error: %v", err)
		return
	}

	switch strings.TrimSpace(header.Type) {
	case "response":
		var rf ResponseFrame
		if err := json.Unmarshal(payload, &rf); err != nil {
			log.Printf("[ipc] response frame unmarshal error: %v", err)
			return
		}
		select {
		case c.ResponseCh <- rf:
		default:
			log.Printf("[ipc] response channel full, dropping response for request_id=%s", rf.RequestID)
		}
	case "perception":
		// Parse, strip self from lot_avatars, re-serialise (reeims-221).
		// The C# PerceptionEmitter filter (a.PersistID != avatar.PersistID) can
		// mis-fire when a controlled Sim's entity has an unexpected PersistID at
		// query time. We apply a defensive second pass in the sidecar so that
		// agents never observe themselves in their own lot_avatars list.
		cleaned := filterSelfFromPerception(payload)
		select {
		case c.PerceptionCh <- cleaned:
		default:
			log.Printf("[ipc] perception channel full, dropping event")
		}
	case "pathfind-failed":
		var pf PathfindFailed
		if err := json.Unmarshal(payload, &pf); err != nil {
			log.Printf("[ipc] pathfind-failed frame unmarshal error: %v", err)
			return
		}
		select {
		case c.PathfindFailedCh <- pf:
		default:
			log.Printf("[ipc] pathfind-failed channel full, dropping event for sim_persist_id=%d", pf.SimPersistID)
		}
	case "dialog":
		var de DialogEvent
		if err := json.Unmarshal(payload, &de); err != nil {
			log.Printf("[ipc] dialog frame unmarshal error: %v", err)
			return
		}
		select {
		case c.DialogCh <- de:
		default:
			log.Printf("[ipc] dialog channel full, dropping event dialog_id=%d", de.DialogID)
		}
	case "chat_received":
		var cr ChatReceived
		if err := json.Unmarshal(payload, &cr); err != nil {
			log.Printf("[ipc] chat_received frame unmarshal error: %v", err)
			return
		}
		select {
		case c.ChatCh <- cr:
		default:
			log.Printf("[ipc] chat channel full, dropping event sender_persist_id=%d", cr.SenderPersistID)
		}
	default:
		// Forward unknown JSON types to perception channel for backward compat.
		cp := make([]byte, len(payload))
		copy(cp, payload)
		select {
		case c.PerceptionCh <- cp:
		default:
			log.Printf("[ipc] perception channel full, dropping unknown JSON event type=%q", header.Type)
		}
	}
}

// filterSelfFromPerception removes any lot_avatars entry whose persist_id equals
// the perception's own persist_id. This is a defensive fix for reeims-221: the
// C# PerceptionEmitter may fail to exclude the observing Sim when its entity
// holds an unexpected PersistID at query time (e.g. after VMIPCDriver temporarily
// reassigns PersistIDs). Returns the cleaned JSON; on any parse error the
// original payload is returned unchanged so agents still receive the frame.
func filterSelfFromPerception(payload []byte) []byte {
	var p Perception
	if err := json.Unmarshal(payload, &p); err != nil {
		log.Printf("[ipc] filterSelfFromPerception: unmarshal error: %v", err)
		return payload
	}

	// Fast path: no lot_avatars at all or none match self.
	filtered := p.LotAvatars[:0]
	selfID := p.PersistID
	removed := 0
	for _, la := range p.LotAvatars {
		if la.PersistID == selfID {
			removed++
			continue
		}
		filtered = append(filtered, la)
	}
	if removed == 0 {
		// Nothing to strip — return original bytes to avoid a spurious re-encode.
		return payload
	}

	p.LotAvatars = filtered
	cleaned, err := json.Marshal(p)
	if err != nil {
		log.Printf("[ipc] filterSelfFromPerception: marshal error: %v", err)
		return payload
	}
	log.Printf("[ipc] filterSelfFromPerception: removed %d self-entry(ies) from lot_avatars for persist_id=%d", removed, selfID)
	return cleaned
}

// readFrame reads one length-prefixed frame from the socket.
func (c *Client) readFrame() ([]byte, error) {
	c.mu.Lock()
	conn := c.conn
	c.mu.Unlock()

	if conn == nil {
		return nil, errors.New("not connected")
	}

	// Read 4-byte length prefix
	var lenBuf [4]byte
	if _, err := io.ReadFull(conn, lenBuf[:]); err != nil {
		return nil, fmt.Errorf("read frame length: %w", err)
	}
	payloadLen := binary.LittleEndian.Uint32(lenBuf[:])

	if payloadLen == 0 || payloadLen > maxFrameSize {
		return nil, fmt.Errorf("invalid frame size: %d", payloadLen)
	}

	buf := make([]byte, payloadLen)
	if _, err := io.ReadFull(conn, buf); err != nil {
		return nil, fmt.Errorf("read frame payload: %w", err)
	}

	return buf, nil
}

func (c *Client) handleDisconnect(err error) {
	log.Printf("[ipc] disconnected: %v", err)
	c.mu.Lock()
	if c.conn != nil {
		c.conn.Close()
		c.conn = nil
	}
	c.mu.Unlock()
}
