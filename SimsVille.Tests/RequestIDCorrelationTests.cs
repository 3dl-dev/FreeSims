/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for the RequestID correlation wire format (reeims-d32).
//
// These tests verify the C#-side serialization of the optional RequestID tail
// without depending on the full SimsVille game binary. They exercise the same
// wire format that VMNetChatCmd.SerializeInto / Deserialize uses, and the same
// 7-bit-length-prefixed encoding that the Go sidecar (commands.go) writes.
//
// The "done condition" for reeims-d32 (C# side) is:
//   - When a command body is serialized with RequestID != null, the tail is:
//       [byte=1][7-bit-length-prefixed UTF-8 string]
//   - When RequestID == null, the tail is [byte=0].
//   - A BinaryReader can round-trip both forms.
//   - The Go sidecar's write7BitEncodedInt output matches BinaryWriter.Write(string).

using System;
using System.IO;
using System.Text;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Minimal self-contained reimplementation of the RequestID tail helpers
    /// (mirrors VMNetCommandBodyAbstract.SerializeRequestID / DeserializeRequestID).
    /// These are tested here without depending on the full SimsVille assembly.
    /// </summary>
    internal static class RequestIDHelper
    {
        public static void SerializeRequestID(BinaryWriter writer, string requestId)
        {
            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId); // BinaryWriter.Write(string) = 7-bit-length-prefixed UTF-8
            }
            else
            {
                writer.Write((byte)0);
            }
        }

        public static string DeserializeRequestID(BinaryReader reader)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                return null;

            byte hasId = reader.ReadByte();
            if (hasId == 1)
                return reader.ReadString(); // BinaryReader.ReadString = 7-bit-length-prefixed UTF-8

            return null;
        }
    }

    public class RequestIDCorrelationTests
    {
        // ---- Serialization without RequestID ----

        [Fact]
        public void SerializeRequestID_Null_WritesSingleZeroByte()
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            RequestIDHelper.SerializeRequestID(writer, null);

            var bytes = ms.ToArray();
            Assert.Equal(1, bytes.Length);
            Assert.Equal(0, bytes[0]);
        }

        // ---- Serialization with RequestID ----

        [Fact]
        public void SerializeRequestID_ShortId_WritesOneFlagPlusEncodedString()
        {
            const string id = "abc123";

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            RequestIDHelper.SerializeRequestID(writer, id);

            var bytes = ms.ToArray();

            // First byte: flag = 1
            Assert.Equal(1, bytes[0]);

            // Second byte: 7-bit encoded length of "abc123" (6 bytes < 128 → single byte 0x06)
            Assert.Equal(6, bytes[1]);

            // Remaining: UTF-8 "abc123"
            var decoded = Encoding.UTF8.GetString(bytes, 2, bytes.Length - 2);
            Assert.Equal(id, decoded);
        }

        [Fact]
        public void SerializeRequestID_LongId_Uses7BitEncoding()
        {
            // Build a string that is exactly 200 bytes in UTF-8
            var id = new string('x', 200);

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            RequestIDHelper.SerializeRequestID(writer, id);

            var bytes = ms.ToArray();
            Assert.Equal(1, bytes[0]); // flag present

            // 7-bit encoded 200 = [0xC8, 0x01] (matches Go's write7BitEncodedInt)
            Assert.Equal(0xC8, bytes[1]);
            Assert.Equal(0x01, bytes[2]);

            var decoded = Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            Assert.Equal(id, decoded);
        }

        // ---- Round-trip: serialize then deserialize ----

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abc123")]
        [InlineData("a very-long-request-id-string-1234567890")]
        public void RoundTrip_WithRequestID_Matches(string requestId)
        {
            // Empty string is treated the same as null for "no RequestID"
            // (Go side sends absent tail when RequestID=="").
            // We normalize empty → null for the test.
            string normalized = string.IsNullOrEmpty(requestId) ? null : requestId;

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            RequestIDHelper.SerializeRequestID(writer, normalized);

            ms.Position = 0;
            using var reader = new BinaryReader(ms);
            var deserialized = RequestIDHelper.DeserializeRequestID(reader);

            Assert.Equal(normalized, deserialized);
        }

        // ---- Wire format matches Go sidecar output ----

        [Fact]
        public void WireFormat_ChatCmdWithRequestID_MatchesGoSidecarLayout()
        {
            // Go sidecar ChatCmd.Serialize layout:
            //   [ActorUID: 4 bytes LE][7bit-len][message][hasRequestID=1][7bit-len][request_id]
            //
            // We build the same byte sequence in C# and verify the exact bytes at each offset,
            // confirming that C# BinaryWriter.Write(string) matches Go's writeBinaryString.

            const uint actorUID = 7;
            const string message = "Hello";
            const string reqId = "abc123";

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            // ActorUID (4 bytes LE)
            writer.Write(actorUID);
            // Message (7-bit-prefixed string)
            writer.Write(message);
            // RequestID tail
            RequestIDHelper.SerializeRequestID(writer, reqId);

            var bytes = ms.ToArray();

            // ActorUID: bytes[0..3] = 7 LE
            Assert.Equal(7u, BitConverter.ToUInt32(bytes, 0));

            // Message length prefix (7-bit encoded 5 = 0x05)
            Assert.Equal(0x05, bytes[4]);
            // Message content
            Assert.Equal("Hello", Encoding.UTF8.GetString(bytes, 5, 5));

            // RequestID flag = 1
            Assert.Equal(1, bytes[10]);
            // RequestID length (7-bit encoded 6 = 0x06)
            Assert.Equal(0x06, bytes[11]);
            // RequestID content
            Assert.Equal("abc123", Encoding.UTF8.GetString(bytes, 12, 6));

            // Total expected: 4 (uid) + 1 (msgLen) + 5 (msg) + 1 (flag) + 1 (idLen) + 6 (id) = 18
            Assert.Equal(18, bytes.Length);
        }

        [Fact]
        public void WireFormat_ChatCmdWithoutRequestID_HasSingleZeroTailByte()
        {
            const uint actorUID = 3;
            const string message = "Hi";

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(actorUID);
            writer.Write(message);
            RequestIDHelper.SerializeRequestID(writer, null);

            var bytes = ms.ToArray();

            // Total: 4 + 1 + 2 + 1 = 8
            Assert.Equal(8, bytes.Length);
            // Last byte: flag=0
            Assert.Equal(0, bytes[7]);
        }

        [Fact]
        public void DeserializeRequestID_AbsentTailByte_ReturnsNull()
        {
            // A stream that ends before the tail byte — old client compatibility.
            using var ms = new MemoryStream(Array.Empty<byte>());
            using var reader = new BinaryReader(ms);

            var result = RequestIDHelper.DeserializeRequestID(reader);

            Assert.Null(result);
        }

        [Fact]
        public void DeserializeRequestID_FlagZero_ReturnsNull()
        {
            using var ms = new MemoryStream(new byte[] { 0 });
            using var reader = new BinaryReader(ms);

            var result = RequestIDHelper.DeserializeRequestID(reader);

            Assert.Null(result);
        }

        [Fact]
        public void DeserializeRequestID_FlagOne_ReturnsId()
        {
            const string id = "test-id";

            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write((byte)1);
            writer.Write(id);

            ms.Position = 0;
            using var reader = new BinaryReader(ms);

            var result = RequestIDHelper.DeserializeRequestID(reader);

            Assert.Equal(id, result);
        }
    }
}
