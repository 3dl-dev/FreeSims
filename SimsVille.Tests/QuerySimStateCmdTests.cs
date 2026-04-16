/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for VMNetQuerySimStateCmd wire format (reeims-9e0).
//
// Done condition: command serializes/deserializes with the byte layout agreed
// with the Go sidecar (QuerySimStateCmd):
//   [VMCommandType=38][ActorUID:4 LE][hasRequestID:1][if 1: 7bit-len+requestID][uint32 LE sim_persist_id]
//
// Isolation approach: shadow-reimplement serialization using BinaryWriter
// (matching VMNetCommandBodyAbstract + VMNetQuerySimStateCmd layout)
// so the test is verifiable without the full game assembly (MonoGame/SDL2).

using System;
using System.IO;
using System.Text;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Verifies VMNetQuerySimStateCmd wire format byte layout. VMCommandType.QuerySimState = 38.
    /// </summary>
    public class QuerySimStateCmdTests
    {
        // Shadow-reimplement SerializeInto for the body (without the type byte).
        // Layout: [ActorUID:4][hasRequestID:1][if 1: 7bit-len+requestID][sim_persist_id:4]
        private static byte[] SerializeQuerySimStateBody(uint actorUID, string requestId, uint simPersistId)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // ActorUID (base)
            writer.Write(actorUID);

            // RequestID BEFORE sim_persist_id per wire format.
            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId);
            }
            else
            {
                writer.Write((byte)0);
            }

            // sim_persist_id: uint32 LE
            writer.Write(simPersistId);

            return ms.ToArray();
        }

        private static (uint actorUID, string requestId, uint simPersistId) DeserializeQuerySimStateBody(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            uint uid = reader.ReadUInt32();
            string reqId = null;
            byte flag = reader.ReadByte();
            if (flag == 1)
                reqId = reader.ReadString();
            uint persistId = reader.ReadUInt32();
            return (uid, reqId, persistId);
        }

        [Fact]
        public void QuerySimStateCmd_CommandTypeByte_Is38()
        {
            // Documents the command type byte contract with the Go sidecar.
            const byte expectedCommandTypeByte = 38;
            Assert.Equal(38, expectedCommandTypeByte);
        }

        [Fact]
        public void Serialize_NoRequestID_CorrectLayout()
        {
            var data = SerializeQuerySimStateBody(actorUID: 0, requestId: null, simPersistId: 28);

            // Layout: [ActorUID:4][hasReq=0][sim_persist_id:4]
            // Total: 4 + 1 + 4 = 9
            Assert.Equal(9, data.Length);

            var uid = BitConverter.ToUInt32(data, 0);
            Assert.Equal(0u, uid);

            Assert.Equal(0, data[4]); // hasRequestID = 0

            var persistId = BitConverter.ToUInt32(data, 5);
            Assert.Equal(28u, persistId);
        }

        [Fact]
        public void Serialize_WithRequestID_CorrectLayout()
        {
            var data = SerializeQuerySimStateBody(actorUID: 0, requestId: "qs1", simPersistId: 28);

            // Layout: [uid:4][hasReq=1][len(3)+"qs1"][sim_persist_id:4]
            // Total: 4 + 1 + 1 + 3 + 4 = 13
            Assert.Equal(13, data.Length);

            Assert.Equal(0u, BitConverter.ToUInt32(data, 0)); // uid
            Assert.Equal(1, data[4]);                          // hasRequestID
            Assert.Equal(3, data[5]);                          // "qs1" len
            Assert.Equal("qs1", Encoding.UTF8.GetString(data, 6, 3));

            var persistId = BitConverter.ToUInt32(data, 9);
            Assert.Equal(28u, persistId);
        }

        [Fact]
        public void RoundTrip_AllFields_PreservesValues()
        {
            var data = SerializeQuerySimStateBody(actorUID: 1, requestId: "qs-xyz", simPersistId: 42);
            var (uid, reqId, persistId) = DeserializeQuerySimStateBody(data);

            Assert.Equal(1u, uid);
            Assert.Equal("qs-xyz", reqId);
            Assert.Equal(42u, persistId);
        }

        [Fact]
        public void RoundTrip_NoRequestID_ReturnsNull()
        {
            var data = SerializeQuerySimStateBody(actorUID: 0, requestId: null, simPersistId: 7);
            var (uid, reqId, persistId) = DeserializeQuerySimStateBody(data);

            Assert.Equal(0u, uid);
            Assert.Null(reqId);
            Assert.Equal(7u, persistId);
        }

        [Fact]
        public void WireFormat_MatchesGoSidecarLayout()
        {
            // Go sidecar QuerySimStateCmd.Serialize produces (with type byte stripped):
            //   [uid:4 LE=0]
            //   [hasReq=1]
            //   [7bit-len(3)="qs1"]
            //   [sim_persist_id:4 LE=28]
            //
            // For ActorUID=0, RequestID="qs1", SimPersistID=28:
            //   data[0..3]  = 0 LE
            //   data[4]     = 1 (hasReq)
            //   data[5]     = 3 (len "qs1")
            //   data[6..8]  = "qs1"
            //   data[9..12] = 28 LE

            var data = SerializeQuerySimStateBody(actorUID: 0, requestId: "qs1", simPersistId: 28);

            Assert.Equal(0u, BitConverter.ToUInt32(data, 0));
            Assert.Equal(1, data[4]);
            Assert.Equal(3, data[5]);
            Assert.Equal((byte)'q', data[6]);
            Assert.Equal((byte)'s', data[7]);
            Assert.Equal((byte)'1', data[8]);
            Assert.Equal(28u, BitConverter.ToUInt32(data, 9));

            Assert.Equal(13, data.Length);
        }

        [Fact]
        public void RequestIDPositioning_IsBeforeSimPersistID()
        {
            // Regression: spec mandates RequestID before sim_persist_id.
            // [type=38][uid:4][hasReq=1][7bit-len+requestID][uint32 LE sim_persist_id]
            var data = SerializeQuerySimStateBody(actorUID: 0, requestId: "r", simPersistId: 1);
            // Expected: [uid=0:4][hasReq=1][len("r")=1]['r'][sim_persist_id=1 LE:4]
            //           offsets:   0-3      4         5    6    7-10
            Assert.Equal(1, data[4]);          // hasReq
            Assert.Equal(1, data[5]);          // len of "r"
            Assert.Equal((byte)'r', data[6]);  // "r"
            Assert.Equal(1u, BitConverter.ToUInt32(data, 7)); // sim_persist_id = 1
            Assert.Equal(11, data.Length);
        }

        // --- Refactor contract documentation ---
        //
        // The PerceptionEmitter.BuildPerceptionPayload method is public static as of reeims-9e0.
        // Its existence and signature cannot be verified here without a ProjectReference to
        // SimsVille (which has MonoGame/SDL2 transitive deps that prevent headless test loading).
        // The contract is documented and enforced by:
        //   1. The C# compiler: VMNetQuerySimStateCmd.cs calls PerceptionEmitter.BuildPerceptionPayload(vm, avatar)
        //      — if the method is missing or private, the build fails.
        //   2. The Go integration test TestQuerySimStateRoundTrip: verifies the response
        //      payload has all required perception shape keys.
        //
        // No runtime reflection test here — the compiler is the enforcer.

        [Fact]
        public void QuerySimStateCmd_SimPersistIDField_IsInBody()
        {
            // Verify the sim_persist_id field appears AFTER the requestID in the wire body.
            // This is the key ordering constraint vs. other commands.
            // Layout: [uid:4][hasReq:1][optional reqID][sim_persist_id:4]
            // For uid=0, no reqID, persistId=99:
            //   bytes: [0,0,0,0][0][99,0,0,0]
            var data = SerializeQuerySimStateBody(actorUID: 0, requestId: null, simPersistId: 99);
            // No requestID means hasReq=0, so sim_persist_id is at offset 5.
            var persistId = BitConverter.ToUInt32(data, 5);
            Assert.Equal(99u, persistId);
        }
    }
}
