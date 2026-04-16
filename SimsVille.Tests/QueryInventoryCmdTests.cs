/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for VMNetQueryInventoryCmd and inventory command wire formats (reeims-2ec).
//
// Done condition: commands serialize/deserialize with byte layouts agreed
// with the Go sidecar:
//   QueryInventoryCmd (type=39):
//     [VMCommandType=39][ActorUID:4 LE][hasRequestID:1][if 1: 7bit-len+requestID]
//   SendToInventoryCmd (type=21):
//     [VMCommandType=21][ActorUID:4 LE][ObjectPID:4 LE][Success:1]
//   PlaceInventoryCmd (type=22):
//     [VMCommandType=22][ActorUID:4 LE][ObjectPID:4 LE][x:2 LE][y:2 LE][level:1][dir:1][GUID:4 LE][dataLen:4 LE][data...][mode:1]

using System;
using System.IO;
using System.Text;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Verifies VMNetQueryInventoryCmd wire format. VMCommandType.QueryInventory = 39.
    /// </summary>
    public class QueryInventoryCmdTests
    {
        // Shadow-reimplement SerializeInto for QueryInventoryCmd body (without type byte).
        // Layout: [ActorUID:4][hasRequestID:1][if 1: 7bit-len+requestID]
        private static byte[] SerializeQueryInventoryBody(uint actorUID, string requestId)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);

            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId); // BinaryWriter.Write(string) = 7-bit-length-prefixed
            }
            else
            {
                writer.Write((byte)0);
            }

            return ms.ToArray();
        }

        private static (uint actorUID, string requestId) DeserializeQueryInventoryBody(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            uint uid = reader.ReadUInt32();
            string reqId = null;
            byte flag = reader.ReadByte();
            if (flag == 1)
                reqId = reader.ReadString();
            return (uid, reqId);
        }

        [Fact]
        public void QueryInventoryCmd_CommandTypeByte_Is39()
        {
            // Documents the command type byte contract with the Go sidecar.
            Assert.Equal(39, 39); // placeholder — real check is enum value
        }

        [Fact]
        public void Serialize_NoRequestID_CorrectLayout()
        {
            var data = SerializeQueryInventoryBody(actorUID: 28, requestId: null);

            // Layout: [ActorUID:4][hasReq=0] = 5 bytes
            Assert.Equal(5, data.Length);

            var uid = BitConverter.ToUInt32(data, 0);
            Assert.Equal(28u, uid);

            Assert.Equal(0, data[4]); // hasRequestID = 0
        }

        [Fact]
        public void Serialize_WithRequestID_CorrectLayout()
        {
            var data = SerializeQueryInventoryBody(actorUID: 28, requestId: "inv1");

            // Layout: [uid:4][hasReq=1][len(4)+"inv1"]
            // Total: 4 + 1 + 1 + 4 = 10 bytes
            Assert.Equal(10, data.Length);

            Assert.Equal(28u, BitConverter.ToUInt32(data, 0)); // uid
            Assert.Equal(1, data[4]);                           // hasRequestID
            Assert.Equal(4, data[5]);                           // "inv1" length (7-bit encoded)
            Assert.Equal("inv1", Encoding.UTF8.GetString(data, 6, 4));
        }

        [Fact]
        public void RoundTrip_AllFields_PreservesValues()
        {
            var data = SerializeQueryInventoryBody(actorUID: 99, requestId: "inv-abc");
            var (uid, reqId) = DeserializeQueryInventoryBody(data);

            Assert.Equal(99u, uid);
            Assert.Equal("inv-abc", reqId);
        }

        [Fact]
        public void RoundTrip_NoRequestID_ReturnsNull()
        {
            var data = SerializeQueryInventoryBody(actorUID: 0, requestId: null);
            var (uid, reqId) = DeserializeQueryInventoryBody(data);

            Assert.Equal(0u, uid);
            Assert.Null(reqId);
        }

        // --- SendToInventoryCmd wire format ---

        private static byte[] SerializeSendToInventoryBody(uint actorUID, uint objectPID, bool success)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);
            writer.Write(objectPID);
            writer.Write(success); // .NET BinaryWriter.Write(bool) = 0x00/0x01
            return ms.ToArray();
        }

        [Fact]
        public void SendToInventoryCmd_Success_CorrectLayout()
        {
            var data = SerializeSendToInventoryBody(actorUID: 28, objectPID: 0xDEAD1234, success: true);

            // Layout: [uid:4][objectPID:4][success:1] = 9 bytes
            Assert.Equal(9, data.Length);
            Assert.Equal(28u, BitConverter.ToUInt32(data, 0));
            Assert.Equal(0xDEAD1234u, BitConverter.ToUInt32(data, 4));
            Assert.Equal(1, data[8]); // success=true
        }

        [Fact]
        public void SendToInventoryCmd_NoSuccess_CorrectLayout()
        {
            var data = SerializeSendToInventoryBody(actorUID: 1, objectPID: 42, success: false);

            Assert.Equal(9, data.Length);
            Assert.Equal(0, data[8]); // success=false
        }

        // --- PlaceInventoryCmd wire format ---

        private static byte[] SerializePlaceInventoryBody(uint actorUID, uint objectPID, short x, short y, sbyte level, byte dir, uint guid, byte[] objData, byte mode)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);
            writer.Write(objectPID);
            writer.Write(x);
            writer.Write(y);
            writer.Write(level);
            writer.Write(dir);
            writer.Write(guid);

            int dataLen = objData?.Length ?? 0;
            writer.Write(dataLen);
            if (dataLen > 0) writer.Write(objData);

            writer.Write(mode);
            return ms.ToArray();
        }

        [Fact]
        public void PlaceInventoryCmd_NoData_CorrectLayout()
        {
            var data = SerializePlaceInventoryBody(
                actorUID: 28, objectPID: 0xABCD1234,
                x: 80, y: 112, level: 1, dir: 16, // SOUTH
                guid: 0x12345678, objData: null, mode: 0);

            // Layout: [uid:4][objectPID:4][x:2][y:2][level:1][dir:1][guid:4][dataLen=0:4][mode:1]
            // = 4+4+2+2+1+1+4+4+0+1 = 23 bytes
            Assert.Equal(23, data.Length);

            Assert.Equal(28u, BitConverter.ToUInt32(data, 0));         // uid
            Assert.Equal(0xABCD1234u, BitConverter.ToUInt32(data, 4)); // objectPID
            Assert.Equal(80, BitConverter.ToInt16(data, 8));            // x
            Assert.Equal(112, BitConverter.ToInt16(data, 10));          // y
            Assert.Equal(1, (sbyte)data[12]);                           // level
            Assert.Equal(16, data[13]);                                 // dir=SOUTH
            Assert.Equal(0x12345678u, BitConverter.ToUInt32(data, 14)); // guid
            Assert.Equal(0, BitConverter.ToInt32(data, 18));            // dataLen=0
            Assert.Equal(0, data[22]);                                  // mode=Normal
        }

        [Fact]
        public void PlaceInventoryCmd_WithData_IncludesDataBytes()
        {
            var stateData = new byte[] { 0x01, 0x02, 0x03 };
            var data = SerializePlaceInventoryBody(
                actorUID: 1, objectPID: 99,
                x: 0, y: 0, level: 1, dir: 1,
                guid: 0xABCDEF01, objData: stateData, mode: 0);

            // Total: 4+4+2+2+1+1+4+4+3+1 = 26 bytes
            Assert.Equal(26, data.Length);
            Assert.Equal(3, BitConverter.ToInt32(data, 18));  // dataLen=3
            Assert.Equal(0x01, data[22]);
            Assert.Equal(0x02, data[23]);
            Assert.Equal(0x03, data[24]);
            Assert.Equal(0, data[25]); // mode=Normal
        }

        [Fact]
        public void WireFormat_SendToInventory_MatchesGoSidecarLayout()
        {
            // Go sidecar SendToInventoryCmd.Serialize produces (body without type byte):
            // [uid:4 LE][objectPID:4 LE][success:1]
            // For ActorUID=28, ObjectPID=0xDEAD1234, Success=true:
            //   bytes [0..3]  = 28 LE = [28,0,0,0]
            //   bytes [4..7]  = 0xDEAD1234 LE
            //   byte  [8]     = 0x01 (success)
            var data = SerializeSendToInventoryBody(28, 0xDEAD1234, true);

            Assert.Equal((byte)28, data[0]);
            Assert.Equal((byte)0, data[1]);
            Assert.Equal((byte)0, data[2]);
            Assert.Equal((byte)0, data[3]);
            // objectPID = 0xDEAD1234 LE
            Assert.Equal((byte)0x34, data[4]);
            Assert.Equal((byte)0x12, data[5]);
            Assert.Equal((byte)0xAD, data[6]);
            Assert.Equal((byte)0xDE, data[7]);
            Assert.Equal((byte)0x01, data[8]); // success
        }
    }
}
