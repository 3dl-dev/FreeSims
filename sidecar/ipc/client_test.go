/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package ipc

import (
	"encoding/binary"
	"encoding/json"
	"net"
	"os"
	"path/filepath"
	"testing"
	"time"
)

// waitConnected polls until the Client's internal conn is non-nil (Connect
// completed) or the deadline expires. This prevents the race where the OS-level
// accept fires on the server side before the client goroutine has stored c.conn.
func waitConnected(t *testing.T, client *Client) {
	t.Helper()
	deadline := time.Now().Add(2 * time.Second)
	for time.Now().Before(deadline) {
		client.mu.Lock()
		conn := client.conn
		client.mu.Unlock()
		if conn != nil {
			return
		}
		time.Sleep(time.Millisecond)
	}
	t.Fatal("timeout waiting for client.conn to be set")
}

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

	// Wait until the client goroutine has stored conn — the OS-level accept
	// fires on the server before the client goroutine sets c.conn.
	waitConnected(t, client)

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

	// Wait until the client goroutine has stored conn before sending.
	waitConnected(t, client)

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

// TestQueryCatalogRoundTrip simulates the catalog query → response JSON parse path:
//
//  1. Client sends a QueryCatalogCmd with RequestID="qc1" over a real Unix socket.
//  2. Fake-game reads the frame and verifies: type byte=36, category="all", requestID="qc1".
//  3. Fake-game sends back a catalog response with >=1 entry containing all required fields.
//  4. We verify the client dispatches it to ResponseCh and the payload parses correctly.
func TestQueryCatalogRoundTrip(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "catalog.sock")

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

	waitConnected(t, client)

	// Build and send a QueryCatalogCmd with RequestID="qc1"
	const reqID = "qc1"
	cmd := &QueryCatalogCmd{ActorUID: 28, Category: "all", RequestID: reqID}
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

	// Verify: [type=36][ActorUID=28 LE][len(3)+"all"][hasRequestID=1][len(3)+"qc1"]
	if frameBuf[0] != byte(CmdQueryCatalog) {
		t.Fatalf("type byte = %d, want %d (CmdQueryCatalog=36)", frameBuf[0], CmdQueryCatalog)
	}
	actorUID := binary.LittleEndian.Uint32(frameBuf[1:5])
	if actorUID != 28 {
		t.Errorf("ActorUID = %d, want 28", actorUID)
	}
	// category "all" at offset 5: len=3, then bytes
	if frameBuf[5] != 0x03 {
		t.Errorf("category length = 0x%02x, want 0x03", frameBuf[5])
	}
	if string(frameBuf[6:9]) != "all" {
		t.Errorf("category = %q, want all", string(frameBuf[6:9]))
	}
	// hasRequestID = 1 at offset 9
	if frameBuf[9] != 1 {
		t.Errorf("hasRequestID = %d, want 1", frameBuf[9])
	}
	// "qc1" length at offset 10
	if frameBuf[10] != 0x03 {
		t.Errorf("requestID length = 0x%02x, want 0x03", frameBuf[10])
	}
	if string(frameBuf[11:14]) != "qc1" {
		t.Errorf("requestID = %q, want qc1", string(frameBuf[11:14]))
	}

	// Simulate game sending a catalog response with 2 sample entries
	catalogJSON := `{"type":"response","request_id":"qc1","status":"ok","payload":{"catalog":[` +
		`{"guid":12345,"name":"Sofa","price":500,"category":"seating","subcategory":"livingroom"},` +
		`{"guid":67890,"name":"Lamp","price":150,"category":"lighting","subcategory":"indoor"}` +
		`]}}`
	respFrame := make([]byte, 4+len(catalogJSON))
	binary.LittleEndian.PutUint32(respFrame[0:4], uint32(len(catalogJSON)))
	copy(respFrame[4:], catalogJSON)
	if _, err := gameConn.Write(respFrame); err != nil {
		t.Fatal("write response frame:", err)
	}

	// Verify client delivers it to ResponseCh
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
		// Verify payload is non-nil and parse as a map.
		// ResponseFrame.Payload is json.RawMessage.
		if rf.Payload == nil {
			t.Fatal("ResponseFrame.Payload is nil")
		}
		var payloadMap map[string]interface{}
		if err := json.Unmarshal(rf.Payload, &payloadMap); err != nil {
			t.Fatalf("unmarshal payload: %v", err)
		}
		// Extract catalog array.
		catalogRaw, ok := payloadMap["catalog"]
		if !ok {
			t.Fatal("payload missing 'catalog' key")
		}
		catalogArr, ok := catalogRaw.([]interface{})
		if !ok {
			t.Fatalf("catalog is not an array, got %T", catalogRaw)
		}
		if len(catalogArr) < 1 {
			t.Fatalf("catalog has %d entries, want >=1", len(catalogArr))
		}
		// Verify first entry has all required fields
		entry, ok := catalogArr[0].(map[string]interface{})
		if !ok {
			t.Fatalf("catalog entry[0] is not an object, got %T", catalogArr[0])
		}
		for _, field := range []string{"guid", "name", "price", "category", "subcategory"} {
			if _, exists := entry[field]; !exists {
				t.Errorf("catalog entry[0] missing required field %q", field)
			}
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for catalog response on ResponseCh")
	}
}

// TestLoadLotRoundTrip (reeims-e8e) simulates the load-lot → queued response path:
//
//  1. Client sends a LoadLotCmd with RequestID="ll1" and HouseXml="house2.xml" over a real Unix socket.
//  2. Fake-game reads the frame and verifies the wire format:
//     [type=37][uid LE][hasReq=1][len+"ll1"][len+"house2.xml"]
//  3. Fake-game sends back a "queued" response carrying payload.house_xml.
//  4. Verify the client dispatches it to ResponseCh with matching request_id.
func TestLoadLotRoundTrip(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "loadlot.sock")

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

	waitConnected(t, client)

	const reqID = "ll1"
	const houseXml = "house2.xml"
	cmd := &LoadLotCmd{ActorUID: 42, HouseXml: houseXml, RequestID: reqID}
	payload, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal("serialize:", err)
	}
	if err := client.SendFrame(payload); err != nil {
		t.Fatal("send:", err)
	}

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

	// Verify: [type=37][ActorUID=42 LE][hasReq=1][len(3)+"ll1"][len(10)+"house2.xml"]
	if frameBuf[0] != byte(CmdLoadLot) {
		t.Fatalf("type byte = %d, want %d (CmdLoadLot=37)", frameBuf[0], CmdLoadLot)
	}
	actorUID := binary.LittleEndian.Uint32(frameBuf[1:5])
	if actorUID != 42 {
		t.Errorf("ActorUID = %d, want 42", actorUID)
	}
	if frameBuf[5] != 1 {
		t.Errorf("hasRequestID = %d, want 1", frameBuf[5])
	}
	if frameBuf[6] != 3 {
		t.Errorf("requestID length = %d, want 3", frameBuf[6])
	}
	if string(frameBuf[7:10]) != reqID {
		t.Errorf("requestID = %q, want %q", string(frameBuf[7:10]), reqID)
	}
	if frameBuf[10] != 10 {
		t.Errorf("house_xml length = %d, want 10", frameBuf[10])
	}
	if string(frameBuf[11:21]) != houseXml {
		t.Errorf("house_xml = %q, want %q", string(frameBuf[11:21]), houseXml)
	}

	// Simulate the game emitting the "queued" response.
	respJSON := `{"type":"response","request_id":"ll1","status":"queued","payload":{"house_xml":"house2.xml"}}`
	respFrame := make([]byte, 4+len(respJSON))
	binary.LittleEndian.PutUint32(respFrame[0:4], uint32(len(respJSON)))
	copy(respFrame[4:], respJSON)
	if _, err := gameConn.Write(respFrame); err != nil {
		t.Fatal("write response frame:", err)
	}

	select {
	case rf := <-client.ResponseCh:
		if rf.RequestID != reqID {
			t.Errorf("ResponseFrame.RequestID = %q, want %q", rf.RequestID, reqID)
		}
		if rf.Status != "queued" {
			t.Errorf("ResponseFrame.Status = %q, want queued", rf.Status)
		}
		var payloadMap map[string]interface{}
		if err := json.Unmarshal(rf.Payload, &payloadMap); err != nil {
			t.Fatalf("unmarshal payload: %v", err)
		}
		got, _ := payloadMap["house_xml"].(string)
		if got != houseXml {
			t.Errorf("payload.house_xml = %q, want %q", got, houseXml)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for load-lot response on ResponseCh")
	}
}

// TestPathfindFailedDispatch (reeims-9e7) verifies that a pathfind-failed JSON
// frame emitted by the game is dispatched to PathfindFailedCh and parsed correctly.
//
//  1. Create a socket pair (fake-game listener + client).
//  2. Fake-game sends a pathfind-failed JSON frame.
//  3. Verify the client dispatches it to PathfindFailedCh with correct fields.
//  4. Verify perception and response channels are NOT populated.
func TestPathfindFailedDispatch(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "pf.sock")

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

	// Send a pathfind-failed JSON frame
	pfJSON := []byte(`{"type":"pathfind-failed","sim_persist_id":42,"target_object_id":7,"reason":"no-path"}`)
	sendFrame(pfJSON)

	// Verify it arrives on PathfindFailedCh
	select {
	case pf := <-client.PathfindFailedCh:
		if pf.Type != "pathfind-failed" {
			t.Errorf("Type = %q, want pathfind-failed", pf.Type)
		}
		if pf.SimPersistID != 42 {
			t.Errorf("SimPersistID = %d, want 42", pf.SimPersistID)
		}
		if pf.TargetObjectID != 7 {
			t.Errorf("TargetObjectID = %d, want 7", pf.TargetObjectID)
		}
		if pf.Reason != "no-path" {
			t.Errorf("Reason = %q, want no-path", pf.Reason)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for pathfind-failed event on PathfindFailedCh")
	}

	// Verify PerceptionCh is empty (event was NOT forwarded there)
	select {
	case p := <-client.PerceptionCh:
		t.Errorf("PerceptionCh unexpectedly received: %s", string(p))
	default:
		// correct: PerceptionCh should be empty
	}

	// Verify ResponseCh is empty
	select {
	case rf := <-client.ResponseCh:
		t.Errorf("ResponseCh unexpectedly received request_id=%s", rf.RequestID)
	default:
		// correct: ResponseCh should be empty
	}
}

// TestPathfindFailedInterleaved verifies that pathfind-failed events are correctly
// dispatched even when interleaved with perception events and tick acks.
func TestPathfindFailedInterleaved(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "pf-interleaved.sock")

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

	// 1. Perception event
	percJSON := []byte(`{"type":"perception","persist_id":1,"sim_id":1,"name":"Daisy"}`)
	sendFrame(percJSON)

	// 2. Pathfind failed
	pfJSON := []byte(`{"type":"pathfind-failed","sim_persist_id":99,"target_object_id":0,"reason":"blocked"}`)
	sendFrame(pfJSON)

	// 3. Tick ack (binary)
	ackFrame := make([]byte, 4+ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[0:4], ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[4:8], 5)
	binary.LittleEndian.PutUint32(ackFrame[8:12], 0)
	binary.LittleEndian.PutUint64(ackFrame[12:20], 123)
	if _, err := gameConn.Write(ackFrame); err != nil {
		t.Fatal("write ack:", err)
	}

	// Verify perception on PerceptionCh
	select {
	case p := <-client.PerceptionCh:
		if string(p) != string(percJSON) {
			t.Errorf("perception = %q, want %q", string(p), string(percJSON))
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for perception")
	}

	// Verify pathfind-failed on PathfindFailedCh
	select {
	case pf := <-client.PathfindFailedCh:
		if pf.SimPersistID != 99 {
			t.Errorf("SimPersistID = %d, want 99", pf.SimPersistID)
		}
		if pf.Reason != "blocked" {
			t.Errorf("Reason = %q, want blocked", pf.Reason)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for pathfind-failed event")
	}

	// Verify tick ack on AckCh
	select {
	case ack := <-client.AckCh:
		if ack.TickID != 5 {
			t.Errorf("TickID = %d, want 5", ack.TickID)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for ack")
	}
}

// TestQuerySimStateRoundTrip (reeims-9e0) simulates the query-sim-state → response path:
//
//  1. Client sends a QuerySimStateCmd with RequestID="qs1" and SimPersistID=28 over a real Unix socket.
//  2. Fake-game reads the frame and verifies wire format:
//     [type=38][uid LE][hasReq=1][len+"qs1"][sim_persist_id=28 LE]
//  3. Fake-game sends back a full perception-shape response.
//  4. We verify the client dispatches it to ResponseCh and the payload contains
//     the required perception keys: motives, position, nearby_objects, lot_avatars, funds, clock.
func TestQuerySimStateRoundTrip(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "qss.sock")

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

	waitConnected(t, client)

	const reqID = "qs1"
	const simPersistID = uint32(28)
	cmd := &QuerySimStateCmd{ActorUID: 0, RequestID: reqID, SimPersistID: simPersistID}
	payload, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal("serialize:", err)
	}
	if err := client.SendFrame(payload); err != nil {
		t.Fatal("send:", err)
	}

	// Read the frame on the game side and verify wire format.
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

	// Verify: [type=38][ActorUID=0 LE][hasReq=1][len(3)+"qs1"][sim_persist_id=28 LE]
	if frameBuf[0] != byte(CmdQuerySimState) {
		t.Fatalf("type byte = %d, want %d (CmdQuerySimState=38)", frameBuf[0], CmdQuerySimState)
	}
	actorUID := binary.LittleEndian.Uint32(frameBuf[1:5])
	if actorUID != 0 {
		t.Errorf("ActorUID = %d, want 0", actorUID)
	}
	if frameBuf[5] != 1 {
		t.Errorf("hasRequestID = %d, want 1", frameBuf[5])
	}
	if frameBuf[6] != 3 {
		t.Errorf("requestID length = %d, want 3", frameBuf[6])
	}
	if string(frameBuf[7:10]) != reqID {
		t.Errorf("requestID = %q, want %q", string(frameBuf[7:10]), reqID)
	}
	gotPersistID := binary.LittleEndian.Uint32(frameBuf[10:14])
	if gotPersistID != simPersistID {
		t.Errorf("sim_persist_id = %d, want %d", gotPersistID, simPersistID)
	}

	// Simulate game sending a full perception-shape response.
	// The payload IS the perception object (not wrapped in a sub-key).
	perceptionPayload := `{` +
		`"type":"perception",` +
		`"persist_id":28,` +
		`"sim_id":5,` +
		`"name":"Daisy",` +
		`"funds":5000,` +
		`"clock":{"hours":10,"minutes":30,"seconds":0,"time_of_day":0,"day":2},` +
		`"motives":{"hunger":50,"comfort":60,"energy":70,"hygiene":80,"bladder":90,"room":40,"social":30,"fun":20,"mood":55},` +
		`"position":{"x":100,"y":200,"level":1},` +
		`"rotation":0.0,` +
		`"current_animation":"idle",` +
		`"action_queue":[],` +
		`"nearby_objects":[],` +
		`"lot_avatars":[]` +
		`}`
	respJSON := `{"type":"response","request_id":"qs1","status":"ok","payload":` + perceptionPayload + `}`
	respFrame := make([]byte, 4+len(respJSON))
	binary.LittleEndian.PutUint32(respFrame[0:4], uint32(len(respJSON)))
	copy(respFrame[4:], respJSON)
	if _, err := gameConn.Write(respFrame); err != nil {
		t.Fatal("write response frame:", err)
	}

	// Verify client delivers it to ResponseCh and the payload has perception shape.
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
		if rf.Payload == nil {
			t.Fatal("ResponseFrame.Payload is nil")
		}

		// Parse payload as a map and verify required perception keys.
		var payloadMap map[string]interface{}
		if err := json.Unmarshal(rf.Payload, &payloadMap); err != nil {
			t.Fatalf("unmarshal payload: %v", err)
		}
		for _, key := range []string{"motives", "position", "nearby_objects", "lot_avatars", "funds", "clock"} {
			if _, exists := payloadMap[key]; !exists {
				t.Errorf("perception payload missing required key %q", key)
			}
		}

		// Verify funds value
		if funds, ok := payloadMap["funds"].(float64); !ok || int(funds) != 5000 {
			t.Errorf("funds = %v, want 5000", payloadMap["funds"])
		}

	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for query-sim-state response on ResponseCh")
	}
}

// TestDialogEventDispatch (reeims-9be) verifies that a dialog JSON frame emitted
// by the game is dispatched to DialogCh and NOT to PerceptionCh or ResponseCh.
//
//  1. Create a socket pair (fake-game listener + client).
//  2. Fake-game sends a dialog JSON frame.
//  3. Verify the client dispatches it to DialogCh with correct fields.
//  4. Verify PerceptionCh and ResponseCh are empty.
func TestDialogEventDispatch(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "dialog.sock")

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

	// Send a dialog JSON frame
	dialogJSON := []byte(`{"type":"dialog","dialog_id":7,"sim_persist_id":42,"title":"Hungry?","text":"You are hungry.","buttons":["Yes","No"]}`)
	sendFrame(dialogJSON)

	// Verify it arrives on DialogCh
	select {
	case de := <-client.DialogCh:
		if de.Type != "dialog" {
			t.Errorf("Type = %q, want dialog", de.Type)
		}
		if de.DialogID != 7 {
			t.Errorf("DialogID = %d, want 7", de.DialogID)
		}
		if de.SimPersistID != 42 {
			t.Errorf("SimPersistID = %d, want 42", de.SimPersistID)
		}
		if de.Title != "Hungry?" {
			t.Errorf("Title = %q, want Hungry?", de.Title)
		}
		if de.Text != "You are hungry." {
			t.Errorf("Text = %q, want You are hungry.", de.Text)
		}
		if len(de.Buttons) != 2 {
			t.Fatalf("len(Buttons) = %d, want 2", len(de.Buttons))
		}
		if de.Buttons[0] != "Yes" {
			t.Errorf("Buttons[0] = %q, want Yes", de.Buttons[0])
		}
		if de.Buttons[1] != "No" {
			t.Errorf("Buttons[1] = %q, want No", de.Buttons[1])
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for dialog event on DialogCh")
	}

	// Verify PerceptionCh is empty
	select {
	case p := <-client.PerceptionCh:
		t.Errorf("PerceptionCh unexpectedly received: %s", string(p))
	default:
	}

	// Verify ResponseCh is empty
	select {
	case rf := <-client.ResponseCh:
		t.Errorf("ResponseCh unexpectedly received request_id=%s", rf.RequestID)
	default:
	}
}

// TestDialogResponseCmdSocketRoundTrip (reeims-9be) verifies the full
// dialog-response command wire path:
//
//  1. Client sends a DialogResponseCmd with dialog_id=5, ResponseCode=0 (yes).
//  2. Fake-game reads the frame and verifies the wire format:
//     [type=11][dialogID=5 LE][responseCode=0][7bit-len(0)] = 7 bytes total.
//  3. Verifies the dialog_id is encoded as ActorUID (bytes[1..4]).
func TestDialogResponseCmdSocketRoundTrip(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "dresp.sock")

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

	waitConnected(t, client)

	// Send a dialog-response command
	cmd := &DialogResponseCmd{DialogID: 5, ResponseCode: 0, ResponseText: ""}
	payload, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal("serialize:", err)
	}
	if err := client.SendFrame(payload); err != nil {
		t.Fatal("send:", err)
	}

	// Read from game side and verify wire format
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

	// Verify: [type=11][dialogID=5 LE][responseCode=0][7bit-len(0)=""] = 7 bytes
	if frameBuf[0] != byte(CmdDialogResponse) {
		t.Fatalf("type byte = %d, want %d (CmdDialogResponse=11)", frameBuf[0], CmdDialogResponse)
	}
	if len(frameBuf) != 6 { // payload is frameLen bytes (no type byte in payload? wait — type IS in payload)
		// Actually: SerializeCommand includes the type byte.
		// frameBuf is the payload AFTER length prefix. Length includes type byte.
		// So frameBuf[0]=type, frameBuf[1..4]=dialogID, frameBuf[5]=code, frameBuf[6]=textLen
	}
	dialogID := binary.LittleEndian.Uint32(frameBuf[1:5])
	if dialogID != 5 {
		t.Errorf("DialogID = %d, want 5", dialogID)
	}
	if frameBuf[5] != 0 {
		t.Errorf("ResponseCode = %d, want 0 (yes)", frameBuf[5])
	}
	// empty response text: 7-bit-len = 0
	if frameBuf[6] != 0x00 {
		t.Errorf("response_text length = 0x%02x, want 0x00", frameBuf[6])
	}
}

// TestDialogEventInterleaved (reeims-9be) verifies that dialog events are correctly
// dispatched even when interleaved with perception events and tick acks.
func TestDialogEventInterleaved(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "dialog-interleaved.sock")

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

	// 1. Perception event
	percJSON := []byte(`{"type":"perception","persist_id":1,"sim_id":1,"name":"Daisy"}`)
	sendFrame(percJSON)

	// 2. Dialog event
	dialogJSON := []byte(`{"type":"dialog","dialog_id":3,"sim_persist_id":1,"title":"Rest?","text":"Sleep?","buttons":["Yes"]}`)
	sendFrame(dialogJSON)

	// 3. Tick ack (binary)
	ackFrame := make([]byte, 4+ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[0:4], ackPayloadSize)
	binary.LittleEndian.PutUint32(ackFrame[4:8], 20)
	binary.LittleEndian.PutUint32(ackFrame[8:12], 0)
	binary.LittleEndian.PutUint64(ackFrame[12:20], 0)
	if _, err := gameConn.Write(ackFrame); err != nil {
		t.Fatal("write ack:", err)
	}

	// Verify perception on PerceptionCh
	select {
	case p := <-client.PerceptionCh:
		if string(p) != string(percJSON) {
			t.Errorf("perception = %q, want %q", string(p), string(percJSON))
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for perception")
	}

	// Verify dialog on DialogCh
	select {
	case de := <-client.DialogCh:
		if de.DialogID != 3 {
			t.Errorf("DialogID = %d, want 3", de.DialogID)
		}
		if de.Title != "Rest?" {
			t.Errorf("Title = %q, want Rest?", de.Title)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for dialog event")
	}

	// Verify tick ack on AckCh
	select {
	case ack := <-client.AckCh:
		if ack.TickID != 20 {
			t.Errorf("TickID = %d, want 20", ack.TickID)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for ack")
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

// --- QueryWallAt socket round-trip test (reeims-d3c) ---
//
// Verifies the full wire path: sidecar serializes QueryWallAtCmd → sends over
// Unix socket → fake-game reads the frame and verifies the bytes → fake-game
// sends back a JSON response frame → client receives it on ResponseCh.
func TestQueryWallAtRoundTrip(t *testing.T) {
	sockPath := filepath.Join(t.TempDir(), "qwall.sock")

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
	go func() { _ = client.Connect() }()

	var gameConn net.Conn
	select {
	case gameConn = <-accepted:
		defer gameConn.Close()
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for client connect")
	}

	waitConnected(t, client)

	const reqID = "qw1"
	cmd := &QueryWallAtCmd{ActorUID: 0, RequestID: reqID, X: 5, Y: 10, Level: 1}
	payload, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal("serialize:", err)
	}
	if err := client.SendFrame(payload); err != nil {
		t.Fatal("send:", err)
	}

	// Read frame on the game side and verify wire format.
	// Layout: [type=40][uid:4][hasReq=1][len(3)+"qw1"][x:2][y:2][level:1] = 15 bytes
	gameConn.SetReadDeadline(time.Now().Add(2 * time.Second))
	var lenBuf [4]byte
	if _, err := readFull(gameConn, lenBuf[:]); err != nil {
		t.Fatal("read frame length:", err)
	}
	frameLen := int(binary.LittleEndian.Uint32(lenBuf[:]))
	frameBuf := make([]byte, frameLen)
	if _, err := readFull(gameConn, frameBuf); err != nil {
		t.Fatal("read frame payload:", err)
	}

	// Verify type byte = 40
	if frameBuf[0] != byte(CmdQueryWallAt) {
		t.Errorf("type byte = %d, want %d (CmdQueryWallAt=40)", frameBuf[0], CmdQueryWallAt)
	}
	// uid = 0 at offsets 1..4
	uid := binary.LittleEndian.Uint32(frameBuf[1:5])
	if uid != 0 {
		t.Errorf("ActorUID = %d, want 0", uid)
	}
	// hasReq = 1 at offset 5
	if frameBuf[5] != 1 {
		t.Errorf("hasRequestID = %d, want 1", frameBuf[5])
	}
	// reqID length = 3 at offset 6
	if frameBuf[6] != 3 {
		t.Errorf("requestID length = %d, want 3", frameBuf[6])
	}
	if string(frameBuf[7:10]) != reqID {
		t.Errorf("requestID = %q, want %q", string(frameBuf[7:10]), reqID)
	}
	// x=5 at offsets 10..11
	if int16(binary.LittleEndian.Uint16(frameBuf[10:12])) != 5 {
		t.Errorf("X = %d, want 5", int16(binary.LittleEndian.Uint16(frameBuf[10:12])))
	}
	// y=10 at offsets 12..13
	if int16(binary.LittleEndian.Uint16(frameBuf[12:14])) != 10 {
		t.Errorf("Y = %d, want 10", int16(binary.LittleEndian.Uint16(frameBuf[12:14])))
	}
	// level=1 at offset 14
	if frameBuf[14] != 1 {
		t.Errorf("Level = %d, want 1", frameBuf[14])
	}

	// Fake-game sends back a JSON response frame with the matching request_id.
	respJSON := `{"type":"response","request_id":"qw1","status":"ok","payload":{"has_wall":true,"segments":3,"top_left_pattern":494,"top_right_pattern":0,"bottom_left_pattern":0,"bottom_right_pattern":0,"top_left_style":1,"top_right_style":0}}`
	respBytes := []byte(respJSON)
	var respLenBuf [4]byte
	binary.LittleEndian.PutUint32(respLenBuf[:], uint32(len(respBytes)))
	gameConn.SetWriteDeadline(time.Now().Add(2 * time.Second))
	if _, err := gameConn.Write(respLenBuf[:]); err != nil {
		t.Fatal("write response length:", err)
	}
	if _, err := gameConn.Write(respBytes); err != nil {
		t.Fatal("write response payload:", err)
	}

	// Client should receive the response on ResponseCh.
	select {
	case rf := <-client.ResponseCh:
		if rf.RequestID != reqID {
			t.Errorf("ResponseFrame.RequestID = %q, want %q", rf.RequestID, reqID)
		}
		if rf.Status != "ok" {
			t.Errorf("ResponseFrame.Status = %q, want ok", rf.Status)
		}
	case <-time.After(2 * time.Second):
		t.Fatal("timeout waiting for response on ResponseCh")
	}
}

// Ensure temp socket cleanup
func TestMain(m *testing.M) {
	os.Exit(m.Run())
}
