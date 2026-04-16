/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package ipc

import (
	"bytes"
	"encoding/binary"
	"fmt"
)

// VMCommandType mirrors the C# VMCommandType enum.
type VMCommandType byte

const (
	CmdSimJoin           VMCommandType = 0
	CmdInteraction       VMCommandType = 1
	CmdArchitecture      VMCommandType = 2
	CmdBuyObject         VMCommandType = 3
	CmdChat              VMCommandType = 4
	CmdBlueprintRestore  VMCommandType = 5
	CmdSimLeave          VMCommandType = 6
	CmdInteractionCancel VMCommandType = 7
	CmdMoveObject        VMCommandType = 8
	CmdDeleteObject      VMCommandType = 9
	CmdGoto              VMCommandType = 10
	CmdChangeLotSize     VMCommandType = 25
	CmdSendToInventory   VMCommandType = 21
	CmdPlaceInventory    VMCommandType = 22
	CmdQueryInventory    VMCommandType = 39
	CmdQueryCatalog      VMCommandType = 36
	CmdLoadLot           VMCommandType = 37
	CmdQuerySimState     VMCommandType = 38
)

// ArchCommandType mirrors the C# VMArchitectureCommandType enum (byte).
type ArchCommandType byte

const (
	ArchWallLine    ArchCommandType = 0
	ArchWallDelete  ArchCommandType = 1
	ArchWallRect    ArchCommandType = 2
	ArchPatternDot  ArchCommandType = 3
	ArchPatternFill ArchCommandType = 4
	ArchFloorRect   ArchCommandType = 5
	ArchFloorFill   ArchCommandType = 6
)

// Command is the interface for all VMNetCommand types.
type Command interface {
	// CmdType returns the VMCommandType byte.
	CmdType() VMCommandType
	// Serialize writes the full command body (ActorUID + fields) into the buffer.
	// It does NOT write the command type byte — that's handled by SerializeCommand.
	Serialize(buf *bytes.Buffer) error
}

// writeRequestIDTail writes the optional RequestID tail:
//
//	[byte hasRequestID=0]          if requestID is empty
//	[byte hasRequestID=1][7-bit-length-prefixed string]   if requestID is non-empty
//
// This MUST be the last thing written into the command body on the Go side,
// matching DeserializeRequestID on the C# side.
func writeRequestIDTail(buf *bytes.Buffer, requestID string) {
	if requestID == "" {
		buf.WriteByte(0)
		return
	}
	buf.WriteByte(1)
	writeBinaryString(buf, requestID)
}

// SerializeCommand produces the wire bytes for a VMNetCommand:
//
//	[1 byte: VMCommandType][command body bytes]
//
// The caller wraps this in a length-prefixed frame via Client.SendFrame.
func SerializeCommand(cmd Command) ([]byte, error) {
	var buf bytes.Buffer
	buf.WriteByte(byte(cmd.CmdType()))
	if err := cmd.Serialize(&buf); err != nil {
		return nil, fmt.Errorf("serialize %T: %w", cmd, err)
	}
	return buf.Bytes(), nil
}

// writeActorUID writes the 4-byte LE ActorUID that starts every command body.
func writeActorUID(buf *bytes.Buffer, uid uint32) {
	b := make([]byte, 4)
	binary.LittleEndian.PutUint32(b, uid)
	buf.Write(b)
}

// writeBinaryString writes a string using .NET BinaryWriter format:
// 7-bit encoded length prefix followed by UTF-8 bytes.
func writeBinaryString(buf *bytes.Buffer, s string) {
	data := []byte(s)
	write7BitEncodedInt(buf, len(data))
	buf.Write(data)
}

// write7BitEncodedInt encodes an integer using .NET's 7-bit variable-length
// encoding. Each byte holds 7 bits of the value; the high bit indicates
// whether more bytes follow.
func write7BitEncodedInt(buf *bytes.Buffer, v int) {
	uv := uint32(v)
	for uv >= 0x80 {
		buf.WriteByte(byte(uv&0x7F) | 0x80)
		uv >>= 7
	}
	buf.WriteByte(byte(uv))
}

// --- Chat command ---

// ChatCmd sends a chat message as a Sim.
type ChatCmd struct {
	ActorUID  uint32
	Message   string
	RequestID string // optional correlation ID; "" = no correlation
}

func (c *ChatCmd) CmdType() VMCommandType { return CmdChat }

func (c *ChatCmd) Serialize(buf *bytes.Buffer) error {
	msg := c.Message
	if len(msg) > 200 {
		msg = msg[:200]
	}
	writeActorUID(buf, c.ActorUID)
	writeBinaryString(buf, msg)
	writeRequestIDTail(buf, c.RequestID)
	return nil
}

// --- Interaction command ---

// InteractionCmd queues an interaction on an object.
// Preempt=1 cancels the currently-running action and pushes this one at
// Maximum priority with Leapfrog — use for agent-initiated interactions that
// need to override Sit/Idle traps.
type InteractionCmd struct {
	ActorUID    uint32
	Interaction uint16
	CalleeID    int16
	Param0      int16
	Preempt     byte
	RequestID   string // optional correlation ID; "" = no correlation
}

func (c *InteractionCmd) CmdType() VMCommandType { return CmdInteraction }

func (c *InteractionCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	b := make([]byte, 7)
	binary.LittleEndian.PutUint16(b[0:2], c.Interaction)
	binary.LittleEndian.PutUint16(b[2:4], uint16(c.CalleeID))
	binary.LittleEndian.PutUint16(b[4:6], uint16(c.Param0))
	b[6] = c.Preempt
	buf.Write(b)
	writeRequestIDTail(buf, c.RequestID)
	return nil
}

// --- Goto command ---

// GotoCmd walks a Sim to a tile position.
type GotoCmd struct {
	ActorUID    uint32
	Interaction uint16
	X           int16
	Y           int16
	Level       int8
}

func (c *GotoCmd) CmdType() VMCommandType { return CmdGoto }

func (c *GotoCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	b := make([]byte, 7)
	binary.LittleEndian.PutUint16(b[0:2], c.Interaction)
	binary.LittleEndian.PutUint16(b[2:4], uint16(c.X))
	binary.LittleEndian.PutUint16(b[4:6], uint16(c.Y))
	b[6] = byte(c.Level)
	buf.Write(b)
	return nil
}

// --- InteractionCancel command ---

// InteractionCancelCmd cancels a queued interaction by its ActionUID.
type InteractionCancelCmd struct {
	ActorUID  uint32
	ActionUID uint16
}

func (c *InteractionCancelCmd) CmdType() VMCommandType { return CmdInteractionCancel }

func (c *InteractionCancelCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	b := make([]byte, 2)
	binary.LittleEndian.PutUint16(b, c.ActionUID)
	buf.Write(b)
	return nil
}

// --- BuyObject command ---
//
// Wire format (after ActorUID):
//
//	[uint32 LE GUID][int16 LE x][int16 LE y][sbyte level][byte dir]
//
// Coordinates are in 1/16 tile units (a tile is 16). Level is the floor
// (1 = ground). Dir is a Direction enum byte (NORTH=1, EAST=4, SOUTH=16,
// WEST=64 in FSO.LotView.Model.Direction; agents pass the raw byte).
// GUID is the catalog GUID of the object. A blacklist check prevents
// buying certain internal-only objects. Verification requires Roommate
// permissions on the caller; an async fund transfer runs before the
// command is actually executed on the lot.
type BuyObjectCmd struct {
	ActorUID uint32
	GUID     uint32
	X        int16
	Y        int16
	Level    int8
	Dir      byte
}

func (c *BuyObjectCmd) CmdType() VMCommandType { return CmdBuyObject }

func (c *BuyObjectCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	b := make([]byte, 10)
	binary.LittleEndian.PutUint32(b[0:4], c.GUID)
	binary.LittleEndian.PutUint16(b[4:6], uint16(c.X))
	binary.LittleEndian.PutUint16(b[6:8], uint16(c.Y))
	b[8] = byte(c.Level)
	b[9] = c.Dir
	buf.Write(b)
	return nil
}

// --- MoveObject command ---
//
// Wire format (after ActorUID):
//
//	[int16 LE ObjectID][int16 LE x][int16 LE y][sbyte level][byte dir]
//
// ObjectID is the runtime VMEntity id (NOT the catalog GUID).
type MoveObjectCmd struct {
	ActorUID uint32
	ObjectID int16
	X        int16
	Y        int16
	Level    int8
	Dir      byte
}

func (c *MoveObjectCmd) CmdType() VMCommandType { return CmdMoveObject }

func (c *MoveObjectCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	b := make([]byte, 8)
	binary.LittleEndian.PutUint16(b[0:2], uint16(c.ObjectID))
	binary.LittleEndian.PutUint16(b[2:4], uint16(c.X))
	binary.LittleEndian.PutUint16(b[4:6], uint16(c.Y))
	b[6] = byte(c.Level)
	b[7] = c.Dir
	buf.Write(b)
	return nil
}

// --- DeleteObject command ---
//
// Wire format (after ActorUID):
//
//	[int16 LE ObjectID][bool CleanupAll]
//
// CleanupAll=true removes the entire multitile group (e.g. a bed) rather
// than a single sub-entity. .NET BinaryWriter.Write(bool) emits a single
// byte (0x00/0x01).
type DeleteObjectCmd struct {
	ActorUID   uint32
	ObjectID   int16
	CleanupAll bool
}

func (c *DeleteObjectCmd) CmdType() VMCommandType { return CmdDeleteObject }

func (c *DeleteObjectCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	b := make([]byte, 3)
	binary.LittleEndian.PutUint16(b[0:2], uint16(c.ObjectID))
	if c.CleanupAll {
		b[2] = 1
	}
	buf.Write(b)
	return nil
}

// --- ChangeLotSize command ---
//
// Wire format (after ActorUID):
//
//	[byte LotSize][byte LotStories]
//
// Indexes into VMBuildableAreaInfo.BuildableSizes. Stories caps at 3.
// Verification requires Owner permissions; the VM will reject down-sizes
// and no-ops. An async funds transaction (base + roommate cost) runs
// before the command actually applies.
type ChangeLotSizeCmd struct {
	ActorUID   uint32
	LotSize    byte
	LotStories byte
}

func (c *ChangeLotSizeCmd) CmdType() VMCommandType { return CmdChangeLotSize }

func (c *ChangeLotSizeCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	buf.WriteByte(c.LotSize)
	buf.WriteByte(c.LotStories)
	return nil
}

// --- Architecture command ---
//
// VMNetArchitectureCmd carries a batch of VMArchitectureCommand ops.
// The list is length-prefixed with an int32 (.NET's BinaryWriter.Write(int)
// emits a 4-byte LE int), then each op serialises as:
//
//	[byte Type][int32 LE x][int32 LE y][sbyte level][int32 LE x2][int32 LE y2][uint16 LE pattern][uint16 LE style]
//
// Type values (ArchCommandType):
//
//	WALL_LINE=0     — draw a wall line: x,y start; x2=length, y2=direction
//	WALL_DELETE=1   — delete walls in a region
//	WALL_RECT=2     — wall rectangle
//	PATTERN_DOT=3   — apply a wall pattern to a single side; x2=side (0-5)
//	PATTERN_FILL=4  — flood-fill a wall pattern
//	FLOOR_RECT=5    — place a floor in a rectangle; x2,y2=width,height
//	FLOOR_FILL=6    — flood-fill a floor
//
// Verification requires BuildBuyRoommate permissions.
type ArchOp struct {
	Type    ArchCommandType
	X       int32
	Y       int32
	Level   int8
	X2      int32
	Y2      int32
	Pattern uint16
	Style   uint16
}

type ArchitectureCmd struct {
	ActorUID uint32
	Ops      []ArchOp
}

func (c *ArchitectureCmd) CmdType() VMCommandType { return CmdArchitecture }

func (c *ArchitectureCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	count := int32(len(c.Ops))
	lenBuf := make([]byte, 4)
	binary.LittleEndian.PutUint32(lenBuf, uint32(count))
	buf.Write(lenBuf)
	for _, op := range c.Ops {
		b := make([]byte, 22)
		b[0] = byte(op.Type)
		binary.LittleEndian.PutUint32(b[1:5], uint32(op.X))
		binary.LittleEndian.PutUint32(b[5:9], uint32(op.Y))
		b[9] = byte(op.Level)
		binary.LittleEndian.PutUint32(b[10:14], uint32(op.X2))
		binary.LittleEndian.PutUint32(b[14:18], uint32(op.Y2))
		binary.LittleEndian.PutUint16(b[18:20], op.Pattern)
		binary.LittleEndian.PutUint16(b[20:22], op.Style)
		buf.Write(b)
	}
	return nil
}

// --- QueryCatalog command ---
//
// Wire format (after VMCommandType byte):
//
//	[ActorUID: 4 bytes LE]
//	[category: 7-bit-length-prefixed UTF-8 string]  — "all" or a FunctionFlags category name
//	[hasRequestID: byte]
//	[if 1: 7-bit-length-prefixed UTF-8 request ID]
//
// The game engine responds with a JSON response frame (type="response") whose
// payload contains a "catalog" array of up to 500 entries:
//
//	{"type":"response","request_id":"...","status":"ok",
//	 "payload":{"catalog":[{"guid":N,"name":"...","price":N,"category":"...","subcategory":"..."}...]}}
//
// The category filter accepts "all" (or "") to return all objects, or a named
// category string (e.g. "seating", "appliances") to filter by FunctionFlags.
type QueryCatalogCmd struct {
	ActorUID  uint32
	Category  string // "all" or a category name; empty means "all"
	RequestID string // optional correlation ID
}

func (c *QueryCatalogCmd) CmdType() VMCommandType { return CmdQueryCatalog }

func (c *QueryCatalogCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	cat := c.Category
	if cat == "" {
		cat = "all"
	}
	writeBinaryString(buf, cat)
	writeRequestIDTail(buf, c.RequestID)
	return nil
}

// --- LoadLot command (reeims-e8e) ---
//
// LoadLotCmd instructs the game to tear down the current lot/VM/blueprint
// and load a different house XML by filename (relative to Content/Houses/).
//
// Wire format (after VMCommandType byte):
//
//	[ActorUID: 4 bytes LE]
//	[hasRequestID: 1 byte]            (0 or 1)
//	[if 1: 7-bit-length-prefixed UTF-8 requestID]
//	[houseXml: 7-bit-length-prefixed UTF-8 string]
//
// Note that RequestID is emitted BEFORE HouseXml here (not after, as the
// standard RequestID tail does). This matches the item spec's layout:
//
//	[type=37][uid:4][hasReq=1][7bit-len+requestID][7bit-len+house_xml]
//
// The game queues the load onto the UI thread via CoreGameScreen.RequestLotLoad
// and immediately emits a "queued" response frame:
//
//	{"type":"response","request_id":"...","status":"queued","payload":{"house_xml":"..."}}
//
// The actual lot reload completes on a subsequent UI tick. A follow-up
// "loaded" response after teardown+reload is NOT emitted in this version —
// agents can detect completion by waiting for a perception event from the
// new lot (different nearby_objects) or by polling query-lot-state.
//
// Sim persistence: the caller's Sim PersistID does NOT survive the lot reload
// in this version — InitTestLot generates a new random MyUID and the old VM's
// avatars are destroyed. The external agent must re-sync via a fresh SimJoin.
type LoadLotCmd struct {
	ActorUID  uint32
	HouseXml  string
	RequestID string // optional correlation ID
}

func (c *LoadLotCmd) CmdType() VMCommandType { return CmdLoadLot }

func (c *LoadLotCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)

	// RequestID tail FIRST (before HouseXml) per the item spec.
	if c.RequestID == "" {
		buf.WriteByte(0)
	} else {
		buf.WriteByte(1)
		writeBinaryString(buf, c.RequestID)
	}

	// HouseXml last.
	writeBinaryString(buf, c.HouseXml)
	return nil
}

// --- QuerySimState command (reeims-9e0) ---
//
// QuerySimStateCmd requests the full perception-shape state for a specific Sim
// by PersistID. The game responds with the same JSON shape as a perception event
// regardless of whether the Sim is idle or busy.
//
// Wire format (after VMCommandType byte):
//
//	[ActorUID: 4 bytes LE]
//	[hasRequestID: 1 byte]            (0 or 1)
//	[if 1: 7-bit-length-prefixed UTF-8 requestID]
//	[sim_persist_id: uint32 LE]
//
// Layout per item spec:
//
//	[type=38][uid:4][flag=1][7bit-len+requestID][uint32 LE sim_persist_id]
//
// Response on success:
//
//	{"type":"response","request_id":"...","status":"ok","payload":{<full perception object>}}
//
// Response on error (e.g. Sim not found):
//
//	{"type":"response","request_id":"...","status":"error","payload":{"error":"sim_not_found"}}
type QuerySimStateCmd struct {
	ActorUID    uint32
	RequestID   string // optional correlation ID
	SimPersistID uint32
}

func (c *QuerySimStateCmd) CmdType() VMCommandType { return CmdQuerySimState }

func (c *QuerySimStateCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)

	// RequestID BEFORE SimPersistID per the wire format spec.
	if c.RequestID == "" {
		buf.WriteByte(0)
	} else {
		buf.WriteByte(1)
		writeBinaryString(buf, c.RequestID)
	}

	// sim_persist_id: uint32 LE
	b := make([]byte, 4)
	binary.LittleEndian.PutUint32(b, c.SimPersistID)
	buf.Write(b)
	return nil
}

// --- SendToInventory command (reeims-2ec) ---
//
// SendToInventoryCmd moves an object (by ObjectPID) from the lot into the
// actor's inventory. The C# Execute() adds the item to vm.MyInventory and
// removes it from the lot when Success=true.
//
// Wire format (after VMCommandType byte):
//
//	[ActorUID: 4 bytes LE]
//	[ObjectPID: uint32 LE]    — PersistID of the object to move to inventory
//	[Success: byte]           — .NET BinaryWriter.Write(bool): 0x00 or 0x01
//
// Note: no RequestID tail — C# VMNetSendToInventoryCmd.Deserialize does not
// call DeserializeRequestID. Use update-inventory after this command to
// confirm the inventory change via a correlated response.
//
// VMCommandType byte: 21
type SendToInventoryCmd struct {
	ActorUID  uint32
	ObjectPID uint32
	Success   bool // true = move to inventory; false = unlock (error path)
}

func (c *SendToInventoryCmd) CmdType() VMCommandType { return CmdSendToInventory }

func (c *SendToInventoryCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	b := make([]byte, 5)
	binary.LittleEndian.PutUint32(b[0:4], c.ObjectPID)
	if c.Success {
		b[4] = 1
	}
	buf.Write(b)
	return nil
}

// --- PlaceInventory command (reeims-2ec) ---
//
// PlaceInventoryCmd places an inventory object back onto the lot at a given
// position. The C# Execute() uses TryPlace() to instantiate the object at
// the given tile coordinates.
//
// Wire format (after VMCommandType byte):
//
//	[ActorUID: 4 bytes LE]
//	[ObjectPID: uint32 LE]    — PersistID of the inventory item to place
//	[x: int16 LE]             — tile x in 1/16 units
//	[y: int16 LE]             — tile y in 1/16 units
//	[level: sbyte]            — floor level (1 = ground)
//	[dir: byte]               — Direction enum byte (NORTH=1, EAST=4, SOUTH=16, WEST=64)
//	[GUID: uint32 LE]         — catalog GUID of the object (needed by TryPlace)
//	[dataLen: int32 LE]       — byte length of saved object state (0 = use fresh instance)
//	[data: []byte]            — optional serialized VMStandaloneObjectMarshal (dataLen bytes)
//	[mode: byte]              — PurchaseMode enum byte (Normal=0, Donate=1, Disallowed=2)
//
// VMCommandType byte: 22
type PlaceInventoryCmd struct {
	ActorUID  uint32
	ObjectPID uint32
	X         int16
	Y         int16
	Level     int8
	Dir       byte
	GUID      uint32
	Data      []byte // nil or empty = instantiate fresh object
	Mode      byte   // PurchaseMode: 0=Normal, 1=Donate, 2=Disallowed
}

func (c *PlaceInventoryCmd) CmdType() VMCommandType { return CmdPlaceInventory }

func (c *PlaceInventoryCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)

	b := make([]byte, 14)
	binary.LittleEndian.PutUint32(b[0:4], c.ObjectPID)
	binary.LittleEndian.PutUint16(b[4:6], uint16(c.X))
	binary.LittleEndian.PutUint16(b[6:8], uint16(c.Y))
	b[8] = byte(c.Level)
	b[9] = c.Dir
	binary.LittleEndian.PutUint32(b[10:14], c.GUID)
	buf.Write(b)

	// dataLen: int32 LE
	dataLen := len(c.Data)
	lenBuf := make([]byte, 4)
	binary.LittleEndian.PutUint32(lenBuf, uint32(dataLen))
	buf.Write(lenBuf)
	if dataLen > 0 {
		buf.Write(c.Data)
	}

	// mode: byte (PurchaseMode)
	buf.WriteByte(c.Mode)
	return nil
}

// --- QueryInventory command (reeims-2ec) ---
//
// QueryInventoryCmd requests the current inventory list for the actor's VM.
// The C# VMNetQueryInventoryCmd.Execute() reads vm.MyInventory and emits a
// JSON response frame via VMIPCDriver.SendInventoryResponse.
//
// Wire format (after VMCommandType byte):
//
//	[ActorUID: 4 bytes LE]
//	[hasRequestID: byte]      — 0 or 1
//	[if 1: 7-bit-length-prefixed UTF-8 requestID]
//
// Response on success:
//
//	{"type":"response","request_id":"...","status":"ok",
//	 "payload":{"inventory":[{"object_pid":N,"guid":N,"name":"...","value":N,"inventory_index":N}...]}}
//
// VMCommandType byte: 39
type QueryInventoryCmd struct {
	ActorUID  uint32
	RequestID string // optional correlation ID
}

func (c *QueryInventoryCmd) CmdType() VMCommandType { return CmdQueryInventory }

func (c *QueryInventoryCmd) Serialize(buf *bytes.Buffer) error {
	writeActorUID(buf, c.ActorUID)
	writeRequestIDTail(buf, c.RequestID)
	return nil
}
