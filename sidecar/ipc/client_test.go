/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package ipc

import (
	"encoding/binary"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// TestClientRoundTrip creates a fake VMIPCDriver (a listening Unix socket),
// connects the Client, sends a chat command, and verifies both the frame
// on the wire and the tick ack coming back.
func TestClientRoundTrip(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "test.sock")

	// Start a fake game listener
	listener, err := net.Listen("unix", sockPath)
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()

	accepted := make(chan net.Conn, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		accepted <- conn
	}()

	// Connect client
	client := NewClient(sockPath)
	defer client.Close()

	go func() {
		if err := client.Connect(); err != nil {
			// expected on Close
		}
	}()

	// Wait for the fake game to accept
	var gameConn net.Conn
	select {
	case gameConn = <-accepted:
		defer gameConn.Close()
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for client connect")
	}

	// Send a chat command
	cmd := &ChatCmd{ActorUID: 1, Message: "Hi"}
	payload, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	if err := client.SendFrame(payload); err != nil {
		t.Fatal(err)
	}

	// Read frame from the game side
	var lenBuf [4]byte
	gameConn.SetReadDeadline(time.Now().Add(2 * time.Second))
	if _, err := readFull(gameConn, lenBuf[:]); err != nil {
		t.Fatal("read frame length:", err)
	}
	frameLen := binary.LittleEndian.Uint32(lenBuf[:])
	frameBuf := make([]byte, frameLen)
	if _, err := readFull(gameConn, frameBuf); err != nil {
		t.Fatal("read frame payload:", err)
	}

	// Verify: [CmdChat=4][ActorUID=1 LE][7bit-len=2]["Hi"]
	if frameBuf[0] != byte(CmdChat) {
		t.Errorf("command type = %d, want %d", frameBuf[0], CmdChat)
	}
	actorUID := binary.LittleEndian.Uint32(frameBuf[1:5])
	if actorUID != 1 {
		t.Errorf("ActorUID = %d, want 1", actorUID)
	}

	// Send a tick ack back
	ackFrame := make([]byte, 4+ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[0:4], ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[4:8], 7)    // tick_id
	binary.LittleEndian.PutUint32(ackFrame[8:12], 1)   // command_count
	binary.LittleEndian.PutUint64(ackFrame[12:20], 42)  // random_seed
	if _, err := gameConn.Write(ackFrame); err != nil {
		t.Fatal("write ack:", err)
	}

	// Read ack from client
	select {
	case ack := <-client.AckCh:
		if ack.TickID != 7 {
			t.Errorf("TickID = %d, want 7", ack.TickID)
		}
		if ack.CommandCount != 1 {
			t.Errorf("CommandCount = %d, want 1", ack.CommandCount)
		}
		if ack.RandomSeed != 42 {
			t.Errorf("RandomSeed = %d, want 42", ack.RandomSeed)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for ack")
	}
}

// TestMixedFrames verifies that the client correctly dispatches binary tick
// acks to AckCh and JSON perception events to PerceptionCh when both frame
// types arrive on the same socket.
func TestMixedFrames(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "mixed.sock")

	listener, err := net.Listen("unix", sockPath)
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()

	accepted := make(chan net.Conn, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		accepted <- conn
	}()

	client := NewClient(sockPath)
	defer client.Close()

	go func() {
		_ = client.Connect()
	}()

	var gameConn net.Conn
	select {
	case gameConn = <-accepted:
		defer gameConn.Close()
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for client connect")
	}

	// 1. Send a binary tick ack (16 bytes)
	ackFrame := make([]byte, 4+ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[0:4], ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[4:8], 10)   // tick_id
	binary.LittleEndian.PutUint32(ackFrame[8:12], 2)    // command_count
	binary.LittleEndian.PutUint64(ackFrame[12:20], 99)  // random_seed
	if _, err := gameConn.Write(ackFrame); err != nil {
		t.Fatal("write ack:", err)
	}

	// 2. Send a JSON perception event
	perception := []byte(`{"type":"perception","persist_id":42,"sim_id":1,"name":"Daisy"}`)
	percFrame := make([]byte, 4+len(perception))
	binary.LittleEndian.PutUint32(percFrame[0:4], uint32(len(perception)))
	copy(percFrame[4:], perception)
	if _, err := gameConn.Write(percFrame); err != nil {
		t.Fatal("write perception:", err)
	}

	// 3. Send another tick ack to confirm continued operation
	ackFrame2 := make([]byte, 4+ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame2[0:4], ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame2[4:8], 11)   // tick_id
	binary.LittleEndian.PutUint32(ackFrame2[8:12], 0)    // command_count
	binary.LittleEndian.PutUint64(ackFrame2[12:20], 100) // random_seed
	if _, err := gameConn.Write(ackFrame2); err != nil {
		t.Fatal("write ack2:", err)
	}

	// Verify first ack
	select {
	case ack := <-client.AckCh:
		if ack.TickID != 10 {
			t.Errorf("ack1 TickID = %d, want 10", ack.TickID)
		}
		if ack.CommandCount != 2 {
			t.Errorf("ack1 CommandCount = %d, want 2", ack.CommandCount)
		}
		if ack.RandomSeed != 99 {
			t.Errorf("ack1 RandomSeed = %d, want 99", ack.RandomSeed)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for ack1")
	}

	// Verify perception
	select {
	case p := <-client.PerceptionCh:
		got := string(p)
		if got != string(perception) {
			t.Errorf("perception = %q, want %q", got, string(perception))
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for perception")
	}

	// Verify second ack
	select {
	case ack := <-client.AckCh:
		if ack.TickID != 11 {
			t.Errorf("ack2 TickID = %d, want 11", ack.TickID)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for ack2")
	}
}

// TestRequestResponseCorrelation verifies the end-to-end correlation protocol:
//
//  1. Client sends a ChatCmd with a RequestID ("abc123") over a real Unix socket.
//  2. The fake-game side reads the frame and verifies the wire format: after the
//     chat body comes [byte=1][7-bit-len][request_id bytes].
//  3. The fake-game side sends back a JSON response frame:
//     {"type":"response","request_id":"abc123","status":"ok","payload":{}}.
//  4. We verify the client dispatches it to ResponseCh with matching RequestID.
func TestRequestResponseCorrelation(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "corr.sock")

	listener, err := net.Listen("unix", sockPath)
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()

	accepted := make(chan net.Conn, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		accepted <- conn
	}()

	client := NewClient(sockPath)
	defer client.Close()

	go func() {
		_ = client.Connect()
	}()

	var gameConn net.Conn
	select {
	case gameConn = <-accepted:
		defer gameConn.Close()
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for client connect")
	}

	// Build and send a ChatCmd with RequestID="abc123"
	const reqID = "abc123"
	cmd := &ChatCmd{ActorUID: 7, Message: "Hello", RequestID: reqID}
	payload, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal("serialize:", err)
	}
	if err := client.SendFrame(payload); err != nil {
		t.Fatal("send:", err)
	}

	// Read the frame on the game side and verify wire format
	gameConn.SetReadDeadline(time.Now().Add(2 * time.Second))
	var lenBuf [4]byte
	if _, err := readFull(gameConn, lenBuf[:]); err != nil {
		t.Fatal("read frame length:", err)
	}
	frameLen := binary.LittleEndian.Uint32(lenBuf[:])
	frameBuf := make([]byte, frameLen)
	if _, err := readFull(gameConn, frameBuf); err != nil {
		t.Fatal("read frame payload:", err)
	}

	// Verify: [CmdChat=4][ActorUID LE][7bit-len]["Hello"][hasRequestID=1][7bit-len]["abc123"]
	if frameBuf[0] != byte(CmdChat) {
		t.Fatalf("type byte = %d, want %d", frameBuf[0], CmdChat)
	}
	actorUID := binary.LittleEndian.Uint32(frameBuf[1:5])
	if actorUID != 7 {
		t.Errorf("ActorUID = %d, want 7", actorUID)
	}
	// Message: 7-bit-len(5=0x05) + "Hello" → offset 5..10
	if frameBuf[5] != 0x05 {
		t.Errorf("msg len byte = 0x%02x, want 0x05", frameBuf[5])
	}
	if string(frameBuf[6:11]) != "Hello" {
		t.Errorf("message = %q, want Hello", string(frameBuf[6:11]))
	}
	// RequestID tail: [hasRequestID=1][7bit-len(6=0x06)]["abc123"]
	if frameBuf[11] != 1 {
		t.Errorf("hasRequestID = %d, want 1", frameBuf[11])
	}
	if frameBuf[12] != byte(len(reqID)) {
		t.Errorf("requestID len byte = %d, want %d", frameBuf[12], len(reqID))
	}
	if string(frameBuf[13:13+len(reqID)]) != reqID {
		t.Errorf("requestID = %q, want %q", string(frameBuf[13:13+len(reqID)]), reqID)
	}

	// Simulate game sending a response frame back
	respJSON := []byte(`{"type":"response","request_id":"abc123","status":"ok","payload":{}}`)
	respFrame := make([]byte, 4+len(respJSON))
	binary.LittleEndian.PutUint32(respFrame[0:4], uint32(len(respJSON)))
	copy(respFrame[4:], respJSON)
	if _, err := gameConn.Write(respFrame); err != nil {
		t.Fatal("write response frame:", err)
	}

	// Verify client delivers it to ResponseCh with matching request_id
	select {
	case rf := <-client.ResponseCh:
		if rf.RequestID != reqID {
			t.Errorf("ResponseFrame.RequestID = %q, want %q", rf.RequestID, reqID)
		}
		if rf.Status != "ok" {
			t.Errorf("ResponseFrame.Status = %q, want ok", rf.Status)
		}
		if rf.Type != "response" {
			t.Errorf("ResponseFrame.Type = %q, want response", rf.Type)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for response on ResponseCh")
	}
}

// TestResponseAndPerceptionInterleaved verifies that response frames and
// perception frames interleaved on the same socket are correctly dispatched
// to their respective channels.
func TestResponseAndPerceptionInterleaved(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "interleaved.sock")

	listener, err := net.Listen("unix", sockPath)
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()

	accepted := make(chan net.Conn, 1)
	go func() {
		conn, err := listener.Accept()
		if err != nil {
			return
		}
		accepted <- conn
	}()

	client := NewClient(sockPath)
	defer client.Close()

	go func() {
		_ = client.Connect()
	}()

	var gameConn net.Conn
	select {
	case gameConn = <-accepted:
		defer gameConn.Close()
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for client connect")
	}

	sendFrame := func(payload []byte) {
		t.Helper()
		frame := make([]byte, 4+len(payload))
		binary.LittleEndian.PutUint32(frame[0:4], uint32(len(payload)))
		copy(frame[4:], payload)
		if _, err := gameConn.Write(frame); err != nil {
			t.Fatalf("write frame: %v", err)
		}
	}

	// 1. Response frame
	resp1 := []byte(`{"type":"response","request_id":"req-1","status":"ok","payload":{}}`)
	sendFrame(resp1)

	// 2. Perception frame
	perc1 := []byte(`{"type":"perception","persist_id":10,"sim_id":2,"name":"Bella"}`)
	sendFrame(perc1)

	// 3. Another response (error)
	resp2 := []byte(`{"type":"response","request_id":"req-2","status":"error","payload":{}}`)
	sendFrame(resp2)

	// Verify response 1
	select {
	case rf := <-client.ResponseCh:
		if rf.RequestID != "req-1" {
			t.Errorf("rf1.RequestID = %q, want req-1", rf.RequestID)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for response 1")
	}

	// Verify perception
	select {
	case p := <-client.PerceptionCh:
		if string(p) != string(perc1) {
			t.Errorf("perception = %q, want %q", string(p), string(perc1))
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for perception")
	}

	// Verify response 2
	select {
	case rf := <-client.ResponseCh:
		if rf.RequestID != "req-2" {
			t.Errorf("rf2.RequestID = %q, want req-2", rf.RequestID)
		}
		if rf.Status != "error" {
			t.Errorf("rf2.Status = %q, want error", rf.Status)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for response 2")
	}
}

func readFull(conn net.Conn, buf []byte) (int, error) {
	total := 0
	for total < len(buf) {
		n, err := conn.Read(buf[total:])
		if err != nil {
			return total + n, err
		}
		total += n
	}
	return total, nil
}

// Ensure temp socket cleanup
func TestMain(m *testing.M) {
	os.Exit(m.Run())
}
