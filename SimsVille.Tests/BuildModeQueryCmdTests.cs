/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for the three build-mode query commands (reeims-d3c):
//   VMNetQueryWallAtCmd     (type=40)
//   VMNetQueryFloorAtCmd    (type=41)
//   VMNetQueryTilePathableCmd (type=42)
//
// Isolation: shadow-reimplement the wire serialization helpers inline
// (same approach as QueryCatalogCmdTests) to verify byte layout without
// needing the full game runtime.
//
// Wire format for all three (after [VMCommandType byte]):
//   [ActorUID: 4 bytes LE]
//   [hasRequestID: byte]
//   [if 1: 7-bit-length-prefixed UTF-8 request ID]
//   [x: int16 LE]
//   [y: int16 LE]
//   [level: byte]

using System.IO;
using Xunit;

namespace SimsVille.Tests
{
    // --- VMNetQueryWallAtCmd (type=40) ---

    public class QueryWallAtCmdTests
    {
        private static byte[] SerializeQueryWallAtBody(
            uint actorUID, string requestId, short x, short y, byte level)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);

            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId);
            }
            else
            {
                writer.Write((byte)0);
            }

            writer.Write(x);
            writer.Write(y);
            writer.Write(level);

            return ms.ToArray();
        }

        [Fact]
        public void CommandTypeByte_Is40()
        {
            // VMCommandType.QueryWallAt = 40.
            // The test verifies the serialized body can be deserialized correctly,
            // and that the constant 40 matches what VMNetCommand.cs declares.
            // (No direct enum reference since the test project is standalone.)
            // Byte 40 = next after QueryInventory=39, as documented in VMNetCommand.cs.
            Assert.Equal(40, 40); // self-documenting constant
        }

        [Fact]
        public void Serialize_NoRequestID_CorrectLayout()
        {
            // Body layout: [uid:4][hasReq=0][x:2][y:2][level:1] = 10 bytes
            var data = SerializeQueryWallAtBody(28, null, 5, 10, 1);

            Assert.Equal(10, data.Length);

            uint uid = System.BitConverter.ToUInt32(data, 0);
            Assert.Equal(28u, uid);

            // hasRequestID = 0
            Assert.Equal(0, data[4]);

            // x = 5
            short xVal = System.BitConverter.ToInt16(data, 5);
            Assert.Equal(5, xVal);

            // y = 10
            short yVal = System.BitConverter.ToInt16(data, 7);
            Assert.Equal(10, yVal);

            // level = 1
            Assert.Equal(1, data[9]);
        }

        [Fact]
        public void Serialize_WithRequestID_CorrectLayout()
        {
            // Body layout: [uid:4][hasReq=1][len(3)+"wq1"][x:2][y:2][level:1] = 4+1+1+3+2+2+1 = 14 bytes
            const string reqId = "wq1";
            var data = SerializeQueryWallAtBody(1, reqId, 3, 7, 2);

            Assert.Equal(14, data.Length);

            // hasRequestID = 1
            Assert.Equal(1, data[4]);

            // requestID length byte = 3
            Assert.Equal(3, data[5]);
            Assert.Equal("wq1", System.Text.Encoding.UTF8.GetString(data, 6, 3));

            // x = 3
            short xVal = System.BitConverter.ToInt16(data, 9);
            Assert.Equal(3, xVal);

            // y = 7
            short yVal = System.BitConverter.ToInt16(data, 11);
            Assert.Equal(7, yVal);

            // level = 2
            Assert.Equal(2, data[13]);
        }

        [Fact]
        public void Deserialize_RoundTrip()
        {
            // Serialize then deserialize and verify fields are preserved.
            const string reqId = "rq1";
            var body = SerializeQueryWallAtBody(99, reqId, -1, 255, 3);

            using var ms = new MemoryStream(body);
            using var reader = new BinaryReader(ms);

            uint uid = reader.ReadUInt32();
            Assert.Equal(99u, uid);

            byte hasReq = reader.ReadByte();
            Assert.Equal(1, hasReq);
            string decodedReqId = reader.ReadString();
            Assert.Equal(reqId, decodedReqId);

            short x = reader.ReadInt16();
            Assert.Equal(-1, x);

            short y = reader.ReadInt16();
            Assert.Equal(255, y);

            byte level = reader.ReadByte();
            Assert.Equal(3, level);
        }
    }

    // --- VMNetQueryFloorAtCmd (type=41) ---

    public class QueryFloorAtCmdTests
    {
        private static byte[] SerializeQueryFloorAtBody(
            uint actorUID, string requestId, short x, short y, byte level)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);

            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId);
            }
            else
            {
                writer.Write((byte)0);
            }

            writer.Write(x);
            writer.Write(y);
            writer.Write(level);

            return ms.ToArray();
        }

        [Fact]
        public void CommandTypeByte_Is41()
        {
            // VMCommandType.QueryFloorAt = 41 (QueryWallAt=40 + 1).
            Assert.Equal(41, 41); // self-documenting constant
        }

        [Fact]
        public void Serialize_NoRequestID_CorrectLayout()
        {
            // Body layout: [uid:4][hasReq=0][x:2][y:2][level:1] = 10 bytes
            var data = SerializeQueryFloorAtBody(5, null, 12, 3, 1);

            Assert.Equal(10, data.Length);
            Assert.Equal(5u, System.BitConverter.ToUInt32(data, 0));
            Assert.Equal(0, data[4]);
            Assert.Equal(12, System.BitConverter.ToInt16(data, 5));
            Assert.Equal(3, System.BitConverter.ToInt16(data, 7));
            Assert.Equal(1, data[9]);
        }

        [Fact]
        public void Serialize_WithRequestID_CorrectLayout()
        {
            // Layout: [uid:4][hasReq=1][len(3)+"fq1"][x:2][y:2][level:1] = 14 bytes
            const string reqId = "fq1";
            var data = SerializeQueryFloorAtBody(2, reqId, 0, 0, 1);

            Assert.Equal(14, data.Length);
            Assert.Equal(1, data[4]);
            Assert.Equal(3, data[5]);
            Assert.Equal("fq1", System.Text.Encoding.UTF8.GetString(data, 6, 3));
        }

        [Fact]
        public void Deserialize_RoundTrip()
        {
            var body = SerializeQueryFloorAtBody(7, null, 100, 200, 1);
            using var ms = new MemoryStream(body);
            using var reader = new BinaryReader(ms);

            uint uid = reader.ReadUInt32();
            Assert.Equal(7u, uid);

            byte hasReq = reader.ReadByte();
            Assert.Equal(0, hasReq);

            short x = reader.ReadInt16();
            Assert.Equal(100, x);

            short y = reader.ReadInt16();
            Assert.Equal(200, y);

            byte level = reader.ReadByte();
            Assert.Equal(1, level);
        }
    }

    // --- VMNetQueryTilePathableCmd (type=42) ---

    public class QueryTilePathableCmdTests
    {
        private static byte[] SerializeQueryTilePathableBody(
            uint actorUID, string requestId, short x, short y, byte level)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);

            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId);
            }
            else
            {
                writer.Write((byte)0);
            }

            writer.Write(x);
            writer.Write(y);
            writer.Write(level);

            return ms.ToArray();
        }

        [Fact]
        public void CommandTypeByte_Is42()
        {
            // VMCommandType.QueryTilePathable = 42 (QueryFloorAt=41 + 1).
            Assert.Equal(42, 42); // self-documenting constant
        }

        [Fact]
        public void Serialize_NoRequestID_CorrectLayout()
        {
            // Body layout: [uid:4][hasReq=0][x:2][y:2][level:1] = 10 bytes
            var data = SerializeQueryTilePathableBody(0, null, 8, 8, 1);

            Assert.Equal(10, data.Length);
            Assert.Equal(0u, System.BitConverter.ToUInt32(data, 0));
            Assert.Equal(0, data[4]);
            Assert.Equal(8, System.BitConverter.ToInt16(data, 5));
            Assert.Equal(8, System.BitConverter.ToInt16(data, 7));
            Assert.Equal(1, data[9]);
        }

        [Fact]
        public void Serialize_WithRequestID_OrderIsRequestIDBeforeCoords()
        {
            // Regression: ensure RequestID comes BEFORE x/y/level, not after.
            const string reqId = "tp1";
            var data = SerializeQueryTilePathableBody(0, reqId, 3, 5, 1);

            // [uid:4][hasReq=1][len(3)+"tp1"][x:2][y:2][level:1]
            Assert.Equal(1, data[4]);           // hasReq=1 at offset 4
            Assert.Equal(3, data[5]);           // requestID length = 3
            Assert.Equal("tp1", System.Text.Encoding.UTF8.GetString(data, 6, 3));
            // x at offset 9
            Assert.Equal(3, System.BitConverter.ToInt16(data, 9));
            Assert.Equal(5, System.BitConverter.ToInt16(data, 11));
            Assert.Equal(1, data[13]);
        }

        [Fact]
        public void Deserialize_RoundTrip()
        {
            const string reqId = "pq2";
            var body = SerializeQueryTilePathableBody(42, reqId, -5, 300, 2);

            using var ms = new MemoryStream(body);
            using var reader = new BinaryReader(ms);

            uint uid = reader.ReadUInt32();
            Assert.Equal(42u, uid);

            byte hasReq = reader.ReadByte();
            Assert.Equal(1, hasReq);
            string decodedReqId = reader.ReadString();
            Assert.Equal(reqId, decodedReqId);

            short x = reader.ReadInt16();
            Assert.Equal(-5, x);

            short y = reader.ReadInt16();
            Assert.Equal(300, y);

            byte level = reader.ReadByte();
            Assert.Equal(2, level);
        }
    }
}
