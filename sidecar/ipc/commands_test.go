/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package ipc

import (
	"bytes"
	"encoding/binary"
	"testing"
)

func TestWrite7BitEncodedInt(t *testing.T) {
	tests := []struct {
		name string
		val  int
		want []byte
	}{
		{"zero", 0, []byte{0x00}},
		{"small", 5, []byte{0x05}},
		{"max-one-byte", 127, []byte{0x7F}},
		{"two-bytes-128", 128, []byte{0x80, 0x01}},
		{"two-bytes-200", 200, []byte{0xC8, 0x01}},
		{"two-bytes-16383", 16383, []byte{0xFF, 0x7F}},
		{"three-bytes", 16384, []byte{0x80, 0x80, 0x01}},
	}
	for _, tt := range tests {
		t.Run(tt.name, func(t *testing.T) {
			var buf bytes.Buffer
			write7BitEncodedInt(&buf, tt.val)
			got := buf.Bytes()
			if !bytes.Equal(got, tt.want) {
				t.Errorf("write7BitEncodedInt(%d) = %v, want %v", tt.val, got, tt.want)
			}
		})
	}
}

func TestChatCmdSerialize(t *testing.T) {
	cmd := &ChatCmd{ActorUID: 42, Message: "Hello"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Expected: [CmdChat=4][ActorUID LE 42][7bit-len 5]["Hello"]
	if data[0] != byte(CmdChat) {
		t.Errorf("type byte = %d, want %d", data[0], CmdChat)
	}

	actorUID := binary.LittleEndian.Uint32(data[1:5])
	if actorUID != 42 {
		t.Errorf("ActorUID = %d, want 42", actorUID)
	}

	// Length prefix: "Hello" is 5 bytes, 7-bit encoded as 0x05
	if data[5] != 0x05 {
		t.Errorf("string length byte = 0x%02x, want 0x05", data[5])
	}

	msg := string(data[6:11])
	if msg != "Hello" {
		t.Errorf("message = %q, want %q", msg, "Hello")
	}
}

func TestInteractionCmdSerialize(t *testing.T) {
	cmd := &InteractionCmd{ActorUID: 1, Interaction: 3, CalleeID: 7, Param0: 0}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	if data[0] != byte(CmdInteraction) {
		t.Errorf("type byte = %d, want %d", data[0], CmdInteraction)
	}

	actorUID := binary.LittleEndian.Uint32(data[1:5])
	if actorUID != 1 {
		t.Errorf("ActorUID = %d, want 1", actorUID)
	}

	interaction := binary.LittleEndian.Uint16(data[5:7])
	if interaction != 3 {
		t.Errorf("Interaction = %d, want 3", interaction)
	}

	calleeID := int16(binary.LittleEndian.Uint16(data[7:9]))
	if calleeID != 7 {
		t.Errorf("CalleeID = %d, want 7", calleeID)
	}

	// Layout: [type=1][uid:4][interaction:2][calleeID:2][param0:2][preempt:1][hasRequestID:1] = 13 total
	if len(data) != 13 {
		t.Errorf("InteractionCmd total bytes = %d, want 13", len(data))
	}
	if data[11] != 0 {
		t.Errorf("Preempt byte = %d, want 0", data[11])
	}
	// tail: hasRequestID=0 (no correlation ID)
	if data[12] != 0 {
		t.Errorf("hasRequestID tail = %d, want 0", data[12])
	}
}

func TestInteractionCmdPreempt(t *testing.T) {
	cmd := &InteractionCmd{ActorUID: 1, Interaction: 3, CalleeID: 7, Preempt: 1}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	if data[11] != 1 {
		t.Errorf("Preempt byte = %d, want 1", data[11])
	}
}

func TestGotoCmdSerialize(t *testing.T) {
	cmd := &GotoCmd{ActorUID: 2, Interaction: 0, X: 100, Y: 200, Level: 1}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	if data[0] != byte(CmdGoto) {
		t.Errorf("type byte = %d, want %d", data[0], CmdGoto)
	}

	x := int16(binary.LittleEndian.Uint16(data[7:9]))
	if x != 100 {
		t.Errorf("X = %d, want 100", x)
	}

	y := int16(binary.LittleEndian.Uint16(data[9:11]))
	if y != 200 {
		t.Errorf("Y = %d, want 200", y)
	}

	level := int8(data[11])
	if level != 1 {
		t.Errorf("Level = %d, want 1", level)
	}
}

func TestBuyObjectCmdSerialize(t *testing.T) {
	cmd := &BuyObjectCmd{
		ActorUID: 28,
		GUID:     0xDEADBEEF,
		X:        10 * 16,
		Y:        15 * 16,
		Level:    1,
		Dir:      16, // SOUTH
	}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Layout: [type=3][uid:4][guid:4][x:2][y:2][level:1][dir:1] = 15 bytes
	if data[0] != byte(CmdBuyObject) {
		t.Errorf("type byte = %d, want %d", data[0], CmdBuyObject)
	}
	if len(data) != 15 {
		t.Fatalf("total bytes = %d, want 15", len(data))
	}
	if binary.LittleEndian.Uint32(data[1:5]) != 28 {
		t.Errorf("ActorUID mismatch")
	}
	if binary.LittleEndian.Uint32(data[5:9]) != 0xDEADBEEF {
		t.Errorf("GUID = %#x, want %#x", binary.LittleEndian.Uint32(data[5:9]), 0xDEADBEEF)
	}
	if int16(binary.LittleEndian.Uint16(data[9:11])) != 160 {
		t.Errorf("X = %d, want 160", int16(binary.LittleEndian.Uint16(data[9:11])))
	}
	if int16(binary.LittleEndian.Uint16(data[11:13])) != 240 {
		t.Errorf("Y = %d, want 240", int16(binary.LittleEndian.Uint16(data[11:13])))
	}
	if int8(data[13]) != 1 {
		t.Errorf("Level = %d, want 1", int8(data[13]))
	}
	if data[14] != 16 {
		t.Errorf("Dir = %d, want 16", data[14])
	}
}

func TestMoveObjectCmdSerialize(t *testing.T) {
	cmd := &MoveObjectCmd{
		ActorUID: 28,
		ObjectID: 44,
		X:        -3,
		Y:        256,
		Level:    2,
		Dir:      1,
	}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Layout: [type=8][uid:4][objID:2][x:2][y:2][level:1][dir:1] = 13 bytes
	if data[0] != byte(CmdMoveObject) {
		t.Errorf("type byte = %d, want %d", data[0], CmdMoveObject)
	}
	if len(data) != 13 {
		t.Fatalf("total bytes = %d, want 13", len(data))
	}
	if int16(binary.LittleEndian.Uint16(data[5:7])) != 44 {
		t.Errorf("ObjectID mismatch")
	}
	if int16(binary.LittleEndian.Uint16(data[7:9])) != -3 {
		t.Errorf("X = %d, want -3", int16(binary.LittleEndian.Uint16(data[7:9])))
	}
	if int16(binary.LittleEndian.Uint16(data[9:11])) != 256 {
		t.Errorf("Y = %d, want 256", int16(binary.LittleEndian.Uint16(data[9:11])))
	}
	if int8(data[11]) != 2 {
		t.Errorf("Level = %d, want 2", int8(data[11]))
	}
	if data[12] != 1 {
		t.Errorf("Dir = %d, want 1", data[12])
	}
}

func TestDeleteObjectCmdSerialize(t *testing.T) {
	cmd := &DeleteObjectCmd{ActorUID: 7, ObjectID: 44, CleanupAll: true}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Layout: [type=9][uid:4][objID:2][cleanup:1] = 8 bytes
	if data[0] != byte(CmdDeleteObject) {
		t.Errorf("type byte = %d, want %d", data[0], CmdDeleteObject)
	}
	if len(data) != 8 {
		t.Fatalf("total bytes = %d, want 8", len(data))
	}
	if int16(binary.LittleEndian.Uint16(data[5:7])) != 44 {
		t.Errorf("ObjectID mismatch")
	}
	if data[7] != 1 {
		t.Errorf("CleanupAll = %d, want 1", data[7])
	}
}

func TestDeleteObjectCmdNoCleanup(t *testing.T) {
	cmd := &DeleteObjectCmd{ActorUID: 7, ObjectID: 44, CleanupAll: false}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	if data[7] != 0 {
		t.Errorf("CleanupAll = %d, want 0", data[7])
	}
}

func TestChangeLotSizeCmdSerialize(t *testing.T) {
	cmd := &ChangeLotSizeCmd{ActorUID: 99, LotSize: 5, LotStories: 2}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Layout: [type=25][uid:4][size:1][stories:1] = 7 bytes
	if data[0] != byte(CmdChangeLotSize) {
		t.Errorf("type byte = %d, want %d", data[0], CmdChangeLotSize)
	}
	if len(data) != 7 {
		t.Fatalf("total bytes = %d, want 7", len(data))
	}
	if binary.LittleEndian.Uint32(data[1:5]) != 99 {
		t.Errorf("ActorUID mismatch")
	}
	if data[5] != 5 {
		t.Errorf("LotSize = %d, want 5", data[5])
	}
	if data[6] != 2 {
		t.Errorf("LotStories = %d, want 2", data[6])
	}
}

func TestArchitectureCmdEmpty(t *testing.T) {
	cmd := &ArchitectureCmd{ActorUID: 1, Ops: nil}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Layout: [type=2][uid:4][count=0:4] = 9 bytes
	if data[0] != byte(CmdArchitecture) {
		t.Errorf("type byte = %d, want %d", data[0], CmdArchitecture)
	}
	if len(data) != 9 {
		t.Fatalf("total bytes = %d, want 9", len(data))
	}
	if binary.LittleEndian.Uint32(data[5:9]) != 0 {
		t.Errorf("count = %d, want 0", binary.LittleEndian.Uint32(data[5:9]))
	}
}

func TestArchitectureCmdSingleOp(t *testing.T) {
	cmd := &ArchitectureCmd{
		ActorUID: 28,
		Ops: []ArchOp{
			{
				Type:    ArchPatternDot,
				X:       5,
				Y:       10,
				Level:   1,
				X2:      0,
				Y2:      0,
				Pattern: 494,
				Style:   0,
			},
		},
	}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Layout: [type=2][uid:4][count=1:4][op:22] = 31 bytes
	// op: [type:1][x:4][y:4][level:1][x2:4][y2:4][pattern:2][style:2]
	if len(data) != 31 {
		t.Fatalf("total bytes = %d, want 31", len(data))
	}
	if binary.LittleEndian.Uint32(data[5:9]) != 1 {
		t.Errorf("count = %d, want 1", binary.LittleEndian.Uint32(data[5:9]))
	}
	// op starts at offset 9
	if data[9] != byte(ArchPatternDot) {
		t.Errorf("op type = %d, want %d", data[9], ArchPatternDot)
	}
	if int32(binary.LittleEndian.Uint32(data[10:14])) != 5 {
		t.Errorf("op X = %d, want 5", int32(binary.LittleEndian.Uint32(data[10:14])))
	}
	if int32(binary.LittleEndian.Uint32(data[14:18])) != 10 {
		t.Errorf("op Y = %d, want 10", int32(binary.LittleEndian.Uint32(data[14:18])))
	}
	if int8(data[18]) != 1 {
		t.Errorf("op Level = %d, want 1", int8(data[18]))
	}
	if int32(binary.LittleEndian.Uint32(data[19:23])) != 0 {
		t.Errorf("op X2 = %d, want 0", int32(binary.LittleEndian.Uint32(data[19:23])))
	}
	if int32(binary.LittleEndian.Uint32(data[23:27])) != 0 {
		t.Errorf("op Y2 = %d, want 0", int32(binary.LittleEndian.Uint32(data[23:27])))
	}
	if binary.LittleEndian.Uint16(data[27:29]) != 494 {
		t.Errorf("op Pattern = %d, want 494", binary.LittleEndian.Uint16(data[27:29]))
	}
	if binary.LittleEndian.Uint16(data[29:31]) != 0 {
		t.Errorf("op Style = %d, want 0", binary.LittleEndian.Uint16(data[29:31]))
	}
}

func TestArchitectureCmdMultipleOps(t *testing.T) {
	cmd := &ArchitectureCmd{
		ActorUID: 1,
		Ops: []ArchOp{
			{Type: ArchWallLine, X: 1, Y: 2, Level: 1, X2: 5, Y2: 0, Pattern: 1, Style: 2},
			{Type: ArchFloorRect, X: 3, Y: 4, Level: 1, X2: 2, Y2: 2, Pattern: 10, Style: 0},
		},
	}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Layout: 1 + 4 + 4 + 22*2 = 53 bytes
	if len(data) != 53 {
		t.Fatalf("total bytes = %d, want 53", len(data))
	}
	if binary.LittleEndian.Uint32(data[5:9]) != 2 {
		t.Errorf("count = %d, want 2", binary.LittleEndian.Uint32(data[5:9]))
	}
	// second op starts at offset 9+22 = 31
	if data[31] != byte(ArchFloorRect) {
		t.Errorf("second op type = %d, want %d", data[31], ArchFloorRect)
	}
}

func TestChatLongMessageTruncation(t *testing.T) {
	long := make([]byte, 300)
	for i := range long {
		long[i] = 'A'
	}
	cmd := &ChatCmd{ActorUID: 1, Message: string(long)}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// After type(1) + actorUID(4) + 7bit-len(2 bytes for 200) + message(200)
	// 7-bit encoded 200 = [0xC8, 0x01]
	lenByte1 := data[5]
	lenByte2 := data[6]
	if lenByte1 != 0xC8 || lenByte2 != 0x01 {
		t.Errorf("encoded length = [0x%02x, 0x%02x], want [0xC8, 0x01]", lenByte1, lenByte2)
	}

	// Total: 1 + 4 + 2 + 200 + 1 (hasRequestID=0) = 208
	if len(data) != 208 {
		t.Errorf("total length = %d, want 208", len(data))
	}
}

// --- QueryCatalogCmd tests (reeims-af0) ---

func TestQueryCatalogCmdSerialize_AllCategory_NoRequestID(t *testing.T) {
	cmd := &QueryCatalogCmd{ActorUID: 1, Category: "all", RequestID: ""}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=36][actorUID:4][len(3)+"all"][hasRequestID=0]
	// Total: 1 + 4 + 1 + 3 + 1 = 10
	if data[0] != byte(CmdQueryCatalog) {
		t.Errorf("type byte = %d, want %d (CmdQueryCatalog)", data[0], CmdQueryCatalog)
	}

	uid := binary.LittleEndian.Uint32(data[1:5])
	if uid != 1 {
		t.Errorf("ActorUID = %d, want 1", uid)
	}

	// category "all": length byte = 3
	if data[5] != 0x03 {
		t.Errorf("category length byte = 0x%02x, want 0x03", data[5])
	}
	cat := string(data[6:9])
	if cat != "all" {
		t.Errorf("category = %q, want %q", cat, "all")
	}

	// hasRequestID = 0
	if data[9] != 0x00 {
		t.Errorf("hasRequestID = 0x%02x, want 0x00", data[9])
	}

	if len(data) != 10 {
		t.Errorf("total length = %d, want 10", len(data))
	}
}

func TestQueryCatalogCmdSerialize_WithRequestID(t *testing.T) {
	cmd := &QueryCatalogCmd{ActorUID: 28, Category: "all", RequestID: "qc1"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=36][actorUID:4][len(3)+"all"][hasRequestID=1][len(3)+"qc1"]
	// Total: 1 + 4 + 1 + 3 + 1 + 1 + 3 = 14
	if data[0] != byte(CmdQueryCatalog) {
		t.Errorf("type byte = %d, want %d", data[0], CmdQueryCatalog)
	}

	uid := binary.LittleEndian.Uint32(data[1:5])
	if uid != 28 {
		t.Errorf("ActorUID = %d, want 28", uid)
	}

	// category "all" at offset 5
	if data[5] != 0x03 {
		t.Errorf("category length = 0x%02x, want 0x03", data[5])
	}

	// hasRequestID flag at offset 9
	if data[9] != 0x01 {
		t.Errorf("hasRequestID = 0x%02x, want 0x01", data[9])
	}

	// "qc1" length byte at offset 10
	if data[10] != 0x03 {
		t.Errorf("requestID length = 0x%02x, want 0x03", data[10])
	}
	reqID := string(data[11:14])
	if reqID != "qc1" {
		t.Errorf("requestID = %q, want %q", reqID, "qc1")
	}

	if len(data) != 14 {
		t.Errorf("total length = %d, want 14", len(data))
	}
}

func TestQueryCatalogCmdSerialize_CategoryFilter(t *testing.T) {
	cmd := &QueryCatalogCmd{ActorUID: 5, Category: "seating", RequestID: ""}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// category "seating" = 7 bytes
	if data[5] != 0x07 {
		t.Errorf("category length = 0x%02x, want 0x07", data[5])
	}
	cat := string(data[6:13])
	if cat != "seating" {
		t.Errorf("category = %q, want %q", cat, "seating")
	}

	// hasRequestID = 0
	if data[13] != 0x00 {
		t.Errorf("hasRequestID = 0x%02x, want 0x00", data[13])
	}
}

func TestQueryCatalogCmdType_Is36(t *testing.T) {
	cmd := &QueryCatalogCmd{}
	if cmd.CmdType() != CmdQueryCatalog {
		t.Errorf("CmdType() = %d, want %d", cmd.CmdType(), CmdQueryCatalog)
	}
	if byte(CmdQueryCatalog) != 36 {
		t.Errorf("CmdQueryCatalog byte value = %d, want 36", byte(CmdQueryCatalog))
	}
}

// --- LoadLotCmd tests (reeims-e8e) ---

func TestLoadLotCmdType_Is37(t *testing.T) {
	cmd := &LoadLotCmd{}
	if cmd.CmdType() != CmdLoadLot {
		t.Errorf("CmdType() = %d, want %d", cmd.CmdType(), CmdLoadLot)
	}
	if byte(CmdLoadLot) != 37 {
		t.Errorf("CmdLoadLot byte value = %d, want 37", byte(CmdLoadLot))
	}
}

func TestLoadLotCmdSerialize_NoRequestID(t *testing.T) {
	cmd := &LoadLotCmd{ActorUID: 1, HouseXml: "house2.xml"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=37][uid:4][hasReq=0][7bit-len(10)+"house2.xml"]
	// Total: 1 + 4 + 1 + 1 + 10 = 17
	if data[0] != byte(CmdLoadLot) {
		t.Errorf("type byte = %d, want %d", data[0], CmdLoadLot)
	}

	uid := binary.LittleEndian.Uint32(data[1:5])
	if uid != 1 {
		t.Errorf("ActorUID = %d, want 1", uid)
	}

	// hasRequestID flag at offset 5 should be 0
	if data[5] != 0 {
		t.Errorf("hasRequestID = %d, want 0", data[5])
	}

	// house_xml length byte (7-bit encoded 10)
	if data[6] != 10 {
		t.Errorf("house_xml length = %d, want 10", data[6])
	}

	houseXml := string(data[7:17])
	if houseXml != "house2.xml" {
		t.Errorf("house_xml = %q, want %q", houseXml, "house2.xml")
	}

	if len(data) != 17 {
		t.Errorf("total length = %d, want 17", len(data))
	}
}

func TestLoadLotCmdSerialize_WithRequestID(t *testing.T) {
	cmd := &LoadLotCmd{ActorUID: 28, HouseXml: "house2.xml", RequestID: "ll1"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=37][uid:4][hasReq=1][7bit-len(3)+"ll1"][7bit-len(10)+"house2.xml"]
	// Total: 1 + 4 + 1 + 1 + 3 + 1 + 10 = 21
	if data[0] != byte(CmdLoadLot) {
		t.Errorf("type byte = %d, want %d", data[0], CmdLoadLot)
	}

	uid := binary.LittleEndian.Uint32(data[1:5])
	if uid != 28 {
		t.Errorf("ActorUID = %d, want 28", uid)
	}

	// hasRequestID flag at offset 5 should be 1
	if data[5] != 1 {
		t.Errorf("hasRequestID = %d, want 1", data[5])
	}

	// requestID length at offset 6
	if data[6] != 3 {
		t.Errorf("requestID length = %d, want 3", data[6])
	}
	if string(data[7:10]) != "ll1" {
		t.Errorf("requestID = %q, want %q", string(data[7:10]), "ll1")
	}

	// house_xml length at offset 10
	if data[10] != 10 {
		t.Errorf("house_xml length = %d, want 10", data[10])
	}
	if string(data[11:21]) != "house2.xml" {
		t.Errorf("house_xml = %q, want %q", string(data[11:21]), "house2.xml")
	}

	if len(data) != 21 {
		t.Errorf("total length = %d, want 21", len(data))
	}
}

// --- QuerySimStateCmd tests (reeims-9e0) ---

func TestQuerySimStateCmdType_Is38(t *testing.T) {
	cmd := &QuerySimStateCmd{}
	if cmd.CmdType() != CmdQuerySimState {
		t.Errorf("CmdType() = %d, want %d", cmd.CmdType(), CmdQuerySimState)
	}
	if byte(CmdQuerySimState) != 38 {
		t.Errorf("CmdQuerySimState byte value = %d, want 38", byte(CmdQuerySimState))
	}
}

func TestQuerySimStateCmdSerialize_NoRequestID(t *testing.T) {
	cmd := &QuerySimStateCmd{ActorUID: 0, SimPersistID: 28}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=38][uid:4][hasReq=0][sim_persist_id:4]
	// Total: 1 + 4 + 1 + 4 = 10
	if data[0] != byte(CmdQuerySimState) {
		t.Errorf("type byte = %d, want %d (CmdQuerySimState=38)", data[0], CmdQuerySimState)
	}

	uid := binary.LittleEndian.Uint32(data[1:5])
	if uid != 0 {
		t.Errorf("ActorUID = %d, want 0", uid)
	}

	if data[5] != 0 {
		t.Errorf("hasRequestID = %d, want 0", data[5])
	}

	persistId := binary.LittleEndian.Uint32(data[6:10])
	if persistId != 28 {
		t.Errorf("SimPersistID = %d, want 28", persistId)
	}

	if len(data) != 10 {
		t.Errorf("total length = %d, want 10", len(data))
	}
}

func TestQuerySimStateCmdSerialize_WithRequestID(t *testing.T) {
	cmd := &QuerySimStateCmd{ActorUID: 0, SimPersistID: 28, RequestID: "qs1"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=38][uid:4][hasReq=1][7bit-len(3)+"qs1"][sim_persist_id:4]
	// Total: 1 + 4 + 1 + 1 + 3 + 4 = 14
	if data[0] != byte(CmdQuerySimState) {
		t.Errorf("type byte = %d, want %d", data[0], CmdQuerySimState)
	}

	uid := binary.LittleEndian.Uint32(data[1:5])
	if uid != 0 {
		t.Errorf("ActorUID = %d, want 0", uid)
	}

	// hasRequestID at offset 5
	if data[5] != 1 {
		t.Errorf("hasRequestID = %d, want 1", data[5])
	}

	// requestID length at offset 6
	if data[6] != 3 {
		t.Errorf("requestID length = %d, want 3", data[6])
	}
	if string(data[7:10]) != "qs1" {
		t.Errorf("requestID = %q, want %q", string(data[7:10]), "qs1")
	}

	// sim_persist_id at offset 10
	persistId := binary.LittleEndian.Uint32(data[10:14])
	if persistId != 28 {
		t.Errorf("SimPersistID = %d, want 28", persistId)
	}

	if len(data) != 14 {
		t.Errorf("total length = %d, want 14", len(data))
	}
}

func TestQuerySimStateCmdSerialize_RequestIDBeforeSimPersistID(t *testing.T) {
	// Regression: the wire format mandates RequestID before sim_persist_id:
	// [type=38][uid:4][flag=1][7bit-len+requestID][uint32 LE sim_persist_id]
	cmd := &QuerySimStateCmd{ActorUID: 0, SimPersistID: 1, RequestID: "r"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Expected bytes:
	//   data[0] = 38 (type)
	//   data[1..4] = 0 (uid LE)
	//   data[5]   = 1 (hasReq)
	//   data[6]   = 1 (len("r"))
	//   data[7]   = 'r'
	//   data[8..11] = 1 LE (sim_persist_id)
	if data[7] != 'r' {
		t.Errorf("byte after requestID length should be 'r', got 0x%02x", data[7])
	}
	persistId := binary.LittleEndian.Uint32(data[8:12])
	if persistId != 1 {
		t.Errorf("SimPersistID = %d, want 1", persistId)
	}
	if len(data) != 12 {
		t.Errorf("total length = %d, want 12", len(data))
	}
}

func TestLoadLotCmdSerialize_RequestIDBeforeHouseXml(t *testing.T) {
	// Regression test: the item spec is explicit that the layout is
	// [type=37][uid:4][hasReq=1][7bit-len+requestID][7bit-len+house_xml]
	// i.e. RequestID comes BEFORE HouseXml (not after, as the standard
	// RequestID tail does in other commands). This catches a swap.
	cmd := &LoadLotCmd{ActorUID: 1, HouseXml: "x", RequestID: "r"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// Expected bytes:
	//   data[0] = 37 (type)
	//   data[1..4] = 1 (uid LE)
	//   data[5]   = 1 (hasReq)
	//   data[6]   = 1 (len("r"))
	//   data[7]   = 'r'
	//   data[8]   = 1 (len("x"))
	//   data[9]   = 'x'
	// If we serialized in the wrong order (house_xml first), data[6] would be 1
	// but data[7] would be 'x' (not 'r').
	if data[7] != 'r' {
		t.Errorf("byte after requestID length should be 'r', got 0x%02x", data[7])
	}
	if data[9] != 'x' {
		t.Errorf("byte after house_xml length should be 'x', got 0x%02x", data[9])
	}
}

// --- Inventory command tests (reeims-2ec) ---

// SendToInventoryCmd wire format: [type=21][uid:4][objectPID:4][success:1] = 10 bytes

func TestSendToInventoryCmdType_Is21(t *testing.T) {
	cmd := &SendToInventoryCmd{}
	if cmd.CmdType() != CmdSendToInventory {
		t.Errorf("CmdType() = %d, want %d", cmd.CmdType(), CmdSendToInventory)
	}
	if byte(CmdSendToInventory) != 21 {
		t.Errorf("CmdSendToInventory byte value = %d, want 21", byte(CmdSendToInventory))
	}
}

func TestSendToInventoryCmdSerialize_Success(t *testing.T) {
	cmd := &SendToInventoryCmd{ActorUID: 28, ObjectPID: 0xDEAD1234, Success: true}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=21][uid:4][objectPID:4][success:1] = 10 bytes
	if data[0] != byte(CmdSendToInventory) {
		t.Errorf("type byte = %d, want 21", data[0])
	}
	if len(data) != 10 {
		t.Fatalf("total bytes = %d, want 10", len(data))
	}
	if binary.LittleEndian.Uint32(data[1:5]) != 28 {
		t.Errorf("ActorUID mismatch")
	}
	if binary.LittleEndian.Uint32(data[5:9]) != 0xDEAD1234 {
		t.Errorf("ObjectPID = %#x, want %#x", binary.LittleEndian.Uint32(data[5:9]), uint32(0xDEAD1234))
	}
	// success=true -> BinaryWriter.Write(bool)=0x01
	if data[9] != 0x01 {
		t.Errorf("Success byte = 0x%02x, want 0x01", data[9])
	}
}

func TestSendToInventoryCmdSerialize_NoSuccess(t *testing.T) {
	cmd := &SendToInventoryCmd{ActorUID: 1, ObjectPID: 42, Success: false}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}
	// success=false -> 0x00
	if data[9] != 0x00 {
		t.Errorf("Success byte = 0x%02x, want 0x00", data[9])
	}
}

// PlaceInventoryCmd wire format: [type=22][uid:4][objectPID:4][x:2][y:2][level:1][dir:1][guid:4][dataLen:4][data...][mode:1]
// Minimum (no data): 10 + 14 + 4 + 1 = wait: 1+4+4+2+2+1+1+4+4+0+1 = 24 bytes

func TestPlaceInventoryCmdType_Is22(t *testing.T) {
	cmd := &PlaceInventoryCmd{}
	if cmd.CmdType() != CmdPlaceInventory {
		t.Errorf("CmdType() = %d, want %d", cmd.CmdType(), CmdPlaceInventory)
	}
	if byte(CmdPlaceInventory) != 22 {
		t.Errorf("CmdPlaceInventory byte value = %d, want 22", byte(CmdPlaceInventory))
	}
}

func TestPlaceInventoryCmdSerialize_NoData(t *testing.T) {
	cmd := &PlaceInventoryCmd{
		ActorUID:  28,
		ObjectPID: 0xABCD1234,
		X:         5 * 16,
		Y:         7 * 16,
		Level:     1,
		Dir:       16, // SOUTH
		GUID:      0x12345678,
		Data:      nil,
		Mode:      0, // Normal
	}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=22][uid:4][objectPID:4][x:2][y:2][level:1][dir:1][guid:4][dataLen:4][mode:1]
	// = 1 + 4 + 4 + 2 + 2 + 1 + 1 + 4 + 4 + 0 + 1 = 24 bytes
	if data[0] != byte(CmdPlaceInventory) {
		t.Errorf("type byte = %d, want 22", data[0])
	}
	if len(data) != 24 {
		t.Fatalf("total bytes = %d, want 24", len(data))
	}
	if binary.LittleEndian.Uint32(data[1:5]) != 28 {
		t.Errorf("ActorUID mismatch")
	}
	if binary.LittleEndian.Uint32(data[5:9]) != 0xABCD1234 {
		t.Errorf("ObjectPID mismatch")
	}
	if int16(binary.LittleEndian.Uint16(data[9:11])) != 80 {
		t.Errorf("X = %d, want 80 (5*16)", int16(binary.LittleEndian.Uint16(data[9:11])))
	}
	if int16(binary.LittleEndian.Uint16(data[11:13])) != 112 {
		t.Errorf("Y = %d, want 112 (7*16)", int16(binary.LittleEndian.Uint16(data[11:13])))
	}
	if int8(data[13]) != 1 {
		t.Errorf("Level = %d, want 1", int8(data[13]))
	}
	if data[14] != 16 {
		t.Errorf("Dir = %d, want 16 (SOUTH)", data[14])
	}
	if binary.LittleEndian.Uint32(data[15:19]) != 0x12345678 {
		t.Errorf("GUID mismatch")
	}
	// dataLen = 0
	if binary.LittleEndian.Uint32(data[19:23]) != 0 {
		t.Errorf("dataLen = %d, want 0", binary.LittleEndian.Uint32(data[19:23]))
	}
	// mode = 0 (Normal)
	if data[23] != 0 {
		t.Errorf("Mode = %d, want 0", data[23])
	}
}

func TestPlaceInventoryCmdSerialize_WithData(t *testing.T) {
	stateData := []byte{0x01, 0x02, 0x03}
	cmd := &PlaceInventoryCmd{
		ActorUID:  1,
		ObjectPID: 99,
		X:         0,
		Y:         0,
		Level:     1,
		Dir:       1, // NORTH
		GUID:      0xABCDEF01,
		Data:      stateData,
		Mode:      0,
	}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Total: 1+4+4+2+2+1+1+4+4+3+1 = 27 bytes
	if len(data) != 27 {
		t.Fatalf("total bytes = %d, want 27", len(data))
	}
	// dataLen = 3 at offset 19
	if binary.LittleEndian.Uint32(data[19:23]) != 3 {
		t.Errorf("dataLen = %d, want 3", binary.LittleEndian.Uint32(data[19:23]))
	}
	// data bytes at offset 23-25
	if data[23] != 0x01 || data[24] != 0x02 || data[25] != 0x03 {
		t.Errorf("data bytes = %v, want [1 2 3]", data[23:26])
	}
	// mode at offset 26
	if data[26] != 0 {
		t.Errorf("Mode = %d, want 0", data[26])
	}
}

// QueryInventoryCmd wire format: [type=39][uid:4][hasRequestID:1][optional requestID]

func TestQueryInventoryCmdType_Is39(t *testing.T) {
	cmd := &QueryInventoryCmd{}
	if cmd.CmdType() != CmdQueryInventory {
		t.Errorf("CmdType() = %d, want %d", cmd.CmdType(), CmdQueryInventory)
	}
	if byte(CmdQueryInventory) != 39 {
		t.Errorf("CmdQueryInventory byte value = %d, want 39", byte(CmdQueryInventory))
	}
}

func TestQueryInventoryCmdSerialize_NoRequestID(t *testing.T) {
	cmd := &QueryInventoryCmd{ActorUID: 28, RequestID: ""}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=39][uid:4][hasRequestID=0] = 6 bytes
	if data[0] != byte(CmdQueryInventory) {
		t.Errorf("type byte = %d, want 39", data[0])
	}
	if len(data) != 6 {
		t.Fatalf("total bytes = %d, want 6", len(data))
	}
	if binary.LittleEndian.Uint32(data[1:5]) != 28 {
		t.Errorf("ActorUID = %d, want 28", binary.LittleEndian.Uint32(data[1:5]))
	}
	if data[5] != 0x00 {
		t.Errorf("hasRequestID = 0x%02x, want 0x00", data[5])
	}
}

func TestQueryInventoryCmdSerialize_WithRequestID(t *testing.T) {
	cmd := &QueryInventoryCmd{ActorUID: 28, RequestID: "inv1"}
	data, err := SerializeCommand(cmd)
	if err != nil {
		t.Fatal(err)
	}

	// Layout: [type=39][uid:4][hasRequestID=1][len(4)="inv1"] = 11 bytes
	if data[0] != byte(CmdQueryInventory) {
		t.Errorf("type byte = %d, want 39", data[0])
	}
	if len(data) != 11 {
		t.Fatalf("total bytes = %d, want 11", len(data))
	}
	if data[5] != 0x01 {
		t.Errorf("hasRequestID = 0x%02x, want 0x01", data[5])
	}
	if data[6] != 0x04 {
		t.Errorf("requestID length = 0x%02x, want 0x04", data[6])
	}
	if string(data[7:11]) != "inv1" {
		t.Errorf("requestID = %q, want %q", string(data[7:11]), "inv1")
	}
}
