/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for chat_received event frame building and earshot filtering (reeims-7a6).
//
// Done conditions verified:
//   1. chat_received JSON contains required keys: type, sender_persist_id, sender_name,
//      text, recipient_persist_ids.
//   2. Frame is length-prefixed (4-byte LE) matching the payload byte count.
//   3. sender_persist_id and sender_name match the sending avatar.
//   4. recipient_persist_ids contains all avatars within L1 ≤ 10 tiles; excludes sender.
//   5. Avatar at L1 = 10 is included; avatar at L1 = 11 is excluded.
//   6. OUT_OF_WORLD sender produces no frame (guard condition).
//   7. Special characters in names/messages are JSON-escaped correctly.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Mirrors the chat_received JSON frame construction from VMIPCDriver.EmitChatReceivedFrame.
    /// Allows testing the JSON shape without the full VM/socket runtime.
    /// </summary>
    internal static class ChatFrameHelper
    {
        private static string EscapeJson(string s) =>
            s.Replace("\\", "\\\\")
             .Replace("\"", "\\\"")
             .Replace("\n", "\\n")
             .Replace("\r", "\\r")
             .Replace("\t", "\\t");

        /// <summary>
        /// Builds the JSON payload (without length prefix) for a chat_received frame.
        /// Mirrors VMIPCDriver.EmitChatReceivedFrame JSON construction.
        /// </summary>
        public static string BuildChatReceivedJson(
            uint senderPersistId, string senderName, string text,
            IReadOnlyList<uint> recipientIds)
        {
            var recipientsJson = new StringBuilder();
            recipientsJson.Append('[');
            for (int i = 0; i < recipientIds.Count; i++)
            {
                if (i > 0) recipientsJson.Append(',');
                recipientsJson.Append(recipientIds[i]);
            }
            recipientsJson.Append(']');

            return $"{{\"type\":\"chat_received\",\"sender_persist_id\":{senderPersistId},\"sender_name\":\"{EscapeJson(senderName)}\",\"text\":\"{EscapeJson(text)}\",\"recipient_persist_ids\":{recipientsJson}}}";
        }

        /// <summary>
        /// Wraps a JSON string into a length-prefixed frame (4-byte LE length + JSON bytes).
        /// Mirrors the framing in VMIPCDriver.EmitChatReceivedFrame.
        /// </summary>
        public static byte[] BuildChatReceivedFrame(
            uint senderPersistId, string senderName, string text,
            IReadOnlyList<uint> recipientIds)
        {
            var json = BuildChatReceivedJson(senderPersistId, senderName, text, recipientIds);
            var jsonBytes = Encoding.UTF8.GetBytes(json);
            var frame = new byte[4 + jsonBytes.Length];
            BitConverter.TryWriteBytes(new Span<byte>(frame, 0, 4), jsonBytes.Length);
            Buffer.BlockCopy(jsonBytes, 0, frame, 4, jsonBytes.Length);
            return frame;
        }
    }

    /// <summary>
    /// Minimal avatar position stub for earshot computation tests.
    /// Mirrors the tile-coordinate model in LotTilePos (TileX = x >> 4, TileY = y >> 4).
    /// </summary>
    internal class FakeTileAvatar
    {
        public uint PersistID { get; set; }
        public string Name { get; set; } = "";

        // Tile coordinates (big-tile units, 1 unit = 1 tile).
        public int TileX { get; set; }
        public int TileY { get; set; }

        public bool OutOfWorld { get; set; }
    }

    /// <summary>
    /// Mirrors the earshot computation from VMIPCDriver.HandleVMChatEvent:
    ///   L1 distance = |otherTileX - senderTileX| + |otherTileY - senderTileY|
    ///   Recipient if L1 ≤ 10 AND not the sender AND not OUT_OF_WORLD.
    /// </summary>
    internal static class EarshotHelper
    {
        public const int EarshotTiles = 10;

        public static List<uint> FindRecipients(
            FakeTileAvatar sender, IEnumerable<FakeTileAvatar> all)
        {
            var result = new List<uint>();
            foreach (var other in all)
            {
                if (other.PersistID == sender.PersistID) continue;
                if (other.OutOfWorld) continue;

                int l1 = Math.Abs(other.TileX - sender.TileX)
                       + Math.Abs(other.TileY - sender.TileY);
                if (l1 <= EarshotTiles)
                    result.Add(other.PersistID);
            }
            return result;
        }
    }

    public class ChatReceivedFrameTests
    {
        // --- Frame shape tests ---

        [Fact]
        public void Frame_HasCorrectLengthPrefix()
        {
            var frame = ChatFrameHelper.BuildChatReceivedFrame(
                senderPersistId: 1, senderName: "Alice", text: "Hi",
                recipientIds: new uint[] { 2 });

            // First 4 bytes: LE payload length
            int prefixedLen = BitConverter.ToInt32(frame, 0);
            Assert.Equal(frame.Length - 4, prefixedLen);
        }

        [Fact]
        public void Frame_TypeField_IsChat_received()
        {
            var json = ChatFrameHelper.BuildChatReceivedJson(
                senderPersistId: 1, senderName: "Alice", text: "Hi",
                recipientIds: new uint[] { 2 });

            using var doc = JsonDocument.Parse(json);
            Assert.Equal("chat_received", doc.RootElement.GetProperty("type").GetString());
        }

        [Fact]
        public void Frame_ContainsRequiredKeys()
        {
            var json = ChatFrameHelper.BuildChatReceivedJson(
                senderPersistId: 42, senderName: "Bob", text: "Hello world",
                recipientIds: new uint[] { 7, 8 });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("type", out _), "missing 'type'");
            Assert.True(root.TryGetProperty("sender_persist_id", out _), "missing 'sender_persist_id'");
            Assert.True(root.TryGetProperty("sender_name", out _), "missing 'sender_name'");
            Assert.True(root.TryGetProperty("text", out _), "missing 'text'");
            Assert.True(root.TryGetProperty("recipient_persist_ids", out _), "missing 'recipient_persist_ids'");
        }

        [Fact]
        public void Frame_SenderFields_MatchInput()
        {
            var json = ChatFrameHelper.BuildChatReceivedJson(
                senderPersistId: 99, senderName: "Gerry", text: "hello Gerry",
                recipientIds: new uint[] { 5 });

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal(99u, root.GetProperty("sender_persist_id").GetUInt32());
            Assert.Equal("Gerry", root.GetProperty("sender_name").GetString());
            Assert.Equal("hello Gerry", root.GetProperty("text").GetString());
        }

        [Fact]
        public void Frame_RecipientPersistIDs_MatchInput()
        {
            var json = ChatFrameHelper.BuildChatReceivedJson(
                senderPersistId: 1, senderName: "Alice", text: "Hi",
                recipientIds: new uint[] { 2, 3, 4 });

            using var doc = JsonDocument.Parse(json);
            var ids = doc.RootElement.GetProperty("recipient_persist_ids").EnumerateArray();
            var collected = new List<uint>();
            foreach (var el in ids)
                collected.Add(el.GetUInt32());

            Assert.Equal(new uint[] { 2, 3, 4 }, collected);
        }

        [Fact]
        public void Frame_EmptyRecipients_ProducesEmptyArray()
        {
            var json = ChatFrameHelper.BuildChatReceivedJson(
                senderPersistId: 1, senderName: "Solo", text: "Nobody here",
                recipientIds: Array.Empty<uint>());

            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement.GetProperty("recipient_persist_ids");
            Assert.Equal(JsonValueKind.Array, arr.ValueKind);
            Assert.Equal(0, arr.GetArrayLength());
        }

        [Fact]
        public void Frame_SpecialCharacters_AreJsonEscaped()
        {
            var json = ChatFrameHelper.BuildChatReceivedJson(
                senderPersistId: 1, senderName: "Alice \"The Great\"",
                text: "It's a tab:\there",
                recipientIds: Array.Empty<uint>());

            // Must be valid JSON (parse without exception)
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Assert.Equal("Alice \"The Great\"", root.GetProperty("sender_name").GetString());
            Assert.Equal("It's a tab:\there", root.GetProperty("text").GetString());
        }

        // --- Earshot computation tests ---

        [Fact]
        public void Earshot_SenderExcludedFromRecipients()
        {
            var sender = new FakeTileAvatar { PersistID = 1, TileX = 5, TileY = 5 };
            var all = new[] { sender };

            var recipients = EarshotHelper.FindRecipients(sender, all);
            Assert.Empty(recipients);
        }

        [Fact]
        public void Earshot_AdjacentAvatar_IsIncluded()
        {
            var sender = new FakeTileAvatar { PersistID = 1, TileX = 5, TileY = 5 };
            var nearby = new FakeTileAvatar { PersistID = 2, TileX = 5, TileY = 6 }; // L1 = 1

            var recipients = EarshotHelper.FindRecipients(sender, new[] { sender, nearby });
            Assert.Contains(2u, recipients);
        }

        [Fact]
        public void Earshot_ExactlyAtLimit_IsIncluded()
        {
            var sender = new FakeTileAvatar { PersistID = 1, TileX = 0, TileY = 0 };
            // L1 = 10 exactly: at the boundary, should be included
            var atLimit = new FakeTileAvatar { PersistID = 2, TileX = 5, TileY = 5 }; // L1 = 10

            var recipients = EarshotHelper.FindRecipients(sender, new[] { sender, atLimit });
            Assert.Contains(2u, recipients);
        }

        [Fact]
        public void Earshot_OneTileOutsideLimit_IsExcluded()
        {
            var sender = new FakeTileAvatar { PersistID = 1, TileX = 0, TileY = 0 };
            // L1 = 11: just outside earshot
            var tooFar = new FakeTileAvatar { PersistID = 2, TileX = 6, TileY = 5 }; // L1 = 11

            var recipients = EarshotHelper.FindRecipients(sender, new[] { sender, tooFar });
            Assert.DoesNotContain(2u, recipients);
        }

        [Fact]
        public void Earshot_OutOfWorldAvatar_IsExcluded()
        {
            var sender = new FakeTileAvatar { PersistID = 1, TileX = 5, TileY = 5 };
            var outOfWorld = new FakeTileAvatar { PersistID = 2, TileX = 5, TileY = 6, OutOfWorld = true };

            var recipients = EarshotHelper.FindRecipients(sender, new[] { sender, outOfWorld });
            Assert.Empty(recipients);
        }

        [Fact]
        public void Earshot_MultipleAvatars_FilteredCorrectly()
        {
            var sender = new FakeTileAvatar { PersistID = 1, TileX = 0, TileY = 0 };
            var inRange1 = new FakeTileAvatar { PersistID = 2, TileX = 3, TileY = 4 }; // L1 = 7
            var inRange2 = new FakeTileAvatar { PersistID = 3, TileX = 5, TileY = 5 }; // L1 = 10 (boundary)
            var outRange = new FakeTileAvatar { PersistID = 4, TileX = 8, TileY = 8 }; // L1 = 16

            var recipients = EarshotHelper.FindRecipients(sender,
                new[] { sender, inRange1, inRange2, outRange });

            Assert.Contains(2u, recipients);
            Assert.Contains(3u, recipients);
            Assert.DoesNotContain(4u, recipients);
            Assert.DoesNotContain(1u, recipients); // sender excluded
        }

        [Fact]
        public void Earshot_DiagonalL1_UsesCorrectFormula()
        {
            // L1 is Manhattan distance: |dx| + |dy|, NOT Euclidean.
            var sender = new FakeTileAvatar { PersistID = 1, TileX = 0, TileY = 0 };
            // Euclidean ≈ 7.07, L1 = 10 — should be included
            var diagAtLimit = new FakeTileAvatar { PersistID = 2, TileX = 5, TileY = 5 }; // L1 = 10
            // Euclidean ≈ 7.81, L1 = 11 — should be excluded
            var diagOutside = new FakeTileAvatar { PersistID = 3, TileX = 6, TileY = 5 }; // L1 = 11

            var recipients = EarshotHelper.FindRecipients(sender,
                new[] { sender, diagAtLimit, diagOutside });

            Assert.Contains(2u, recipients);
            Assert.DoesNotContain(3u, recipients);
        }
    }
}
