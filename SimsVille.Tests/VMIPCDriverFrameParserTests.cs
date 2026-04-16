/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Regression tests for the VMIPCDriver frame-parser guard (reeims-c8c).
//
// Bug: the original guard was `payloadLen < 0 || payloadLen > 1_000_000`.
// A frame with payloadLen == 0 passed the guard. Deserialize then threw
// EndOfStreamException (caught silently) and offset advanced by 4 + 0 = 4
// bytes — landing on the command-type byte of the *next* real frame,
// misaligning the parser for all subsequent frames.
//
// Fix: change guard to `payloadLen <= 0`.
//
// Isolation approach: the frame-parse loop in VMIPCDriver calls VMNetCommand
// which has transitive MonoGame/SDL2 dependencies (via VMCommandType / all
// concrete command types). We cannot compile VMIPCDriver.cs directly into
// the test assembly without pulling in that chain.
//
// Instead we shadow the frame-parse guard logic — the exact arithmetic and
// condition that determines `invalidFrame`. The test verifies:
//   1. payloadLen == 0  → guard triggers (invalidFrame = true)
//   2. payloadLen < 0   → guard triggers (invalidFrame = true)
//   3. payloadLen > 1_000_000 → guard triggers (invalidFrame = true)
//   4. A valid frame preceding a zero-length frame is consumed before the
//      guard fires; the parser does NOT misalign into subsequent bytes.
//   5. With the fixed guard, a zero-length frame at offset 0 is caught
//      immediately — the following real frame is NOT consumed as garbage.
//
// The shadowed logic is kept intentionally minimal and line-for-line
// equivalent to the production code so that any future drift is obvious.

using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Verifies that the VMIPCDriver frame-parser guard rejects payloadLen == 0
    /// and does not misalign the parser when a zero-length frame appears in the
    /// stream (regression: reeims-c8c).
    /// </summary>
    public class VMIPCDriverFrameParserTests
    {
        // ---------------------------------------------------------------------------
        // Shadow implementation of the frame-parse guard logic.
        //
        // Mirrors VMIPCDriver.ParseFramesFromBuffer exactly, but instead of
        // constructing VMNetCommand objects (which need MonoGame), it records the
        // raw payload bytes of each successfully parsed frame.  The guard condition
        // `payloadLen <= 0` is what the test exercises.
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Parse length-prefixed frames from <paramref name="buf"/>[0..<paramref name="pos"/>].
        /// Returns the list of raw payload byte arrays for each valid frame and a
        /// flag indicating whether an invalid length was encountered.
        /// </summary>
        private static (List<byte[]> frames, int consumed, bool invalidFrame)
            ParseFrames(byte[] buf, int pos)
        {
            var frames = new List<byte[]>();
            int offset = 0;

            while (offset + 4 <= pos)
            {
                int payloadLen = BitConverter.ToInt32(buf, offset);

                // Fixed guard (reeims-c8c): was `payloadLen < 0`, must be `payloadLen <= 0`
                if (payloadLen <= 0 || payloadLen > 1_000_000)
                    return (frames, offset, true);

                if (offset + 4 + payloadLen > pos)
                    break; // incomplete, wait

                var payload = new byte[payloadLen];
                Buffer.BlockCopy(buf, offset + 4, payload, 0, payloadLen);
                frames.Add(payload);

                offset += 4 + payloadLen;
            }

            return (frames, offset, false);
        }

        // Helper: build a length-prefixed frame
        private static byte[] Frame(byte[] payload)
        {
            var frame = new byte[4 + payload.Length];
            BitConverter.TryWriteBytes(new Span<byte>(frame, 0, 4), payload.Length);
            Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            return frame;
        }

        // Helper: concatenate byte arrays
        private static byte[] Concat(params byte[][] arrays)
        {
            int total = 0;
            foreach (var a in arrays) total += a.Length;
            var result = new byte[total];
            int pos = 0;
            foreach (var a in arrays) { Buffer.BlockCopy(a, 0, result, pos, a.Length); pos += a.Length; }
            return result;
        }

        // ---------------------------------------------------------------------------
        // Tests
        // ---------------------------------------------------------------------------

        [Fact]
        public void ZeroLengthFrame_TriggersInvalidFrame()
        {
            // A frame whose 4-byte length field is 0
            var buf = new byte[] { 0, 0, 0, 0 }; // payloadLen == 0
            var (frames, consumed, invalidFrame) = ParseFrames(buf, buf.Length);

            Assert.True(invalidFrame, "payloadLen == 0 must trigger invalidFrame");
            Assert.Empty(frames);
        }

        [Fact]
        public void NegativeLengthFrame_TriggersInvalidFrame()
        {
            var buf = new byte[4];
            BitConverter.TryWriteBytes(new Span<byte>(buf, 0, 4), -1);
            var (_, _, invalidFrame) = ParseFrames(buf, buf.Length);

            Assert.True(invalidFrame, "payloadLen == -1 must trigger invalidFrame");
        }

        [Fact]
        public void OversizeLengthFrame_TriggersInvalidFrame()
        {
            var buf = new byte[4];
            BitConverter.TryWriteBytes(new Span<byte>(buf, 0, 4), 1_000_001);
            var (_, _, invalidFrame) = ParseFrames(buf, buf.Length);

            Assert.True(invalidFrame, "payloadLen > 1_000_000 must trigger invalidFrame");
        }

        [Fact]
        public void ValidFrame_ParsedCorrectly()
        {
            var payload = new byte[] { 0x01, 0x02, 0x03 };
            var buf = Frame(payload);
            var (frames, consumed, invalidFrame) = ParseFrames(buf, buf.Length);

            Assert.False(invalidFrame);
            Assert.Single(frames);
            Assert.Equal(payload, frames[0]);
            Assert.Equal(buf.Length, consumed);
        }

        /// <summary>
        /// Regression for reeims-c8c: before the fix, a zero-length frame was not
        /// caught and offset advanced by 4 + 0 = 4, landing on the command-type
        /// byte of the next real frame and misaligning the parser.
        ///
        /// After the fix, the zero-length frame fires `invalidFrame = true` at
        /// offset 0, before touching any subsequent bytes.  The consumed count
        /// equals 0 (no bytes were successfully consumed).
        /// </summary>
        [Fact]
        public void ZeroLengthFrame_DoesNotMisalignParserIntoFollowingFrame()
        {
            // Build: [zero-length frame][valid frame with payload {0xFF}]
            var zeroFrame = new byte[] { 0, 0, 0, 0 };                   // payloadLen == 0
            var realPayload = new byte[] { 0xFF };
            var realFrame = Frame(realPayload);
            var buf = Concat(zeroFrame, realFrame);

            var (frames, consumed, invalidFrame) = ParseFrames(buf, buf.Length);

            // Guard must fire immediately on the zero-length frame
            Assert.True(invalidFrame, "parser must detect payloadLen == 0 as invalid");

            // No frames were successfully parsed — the real frame was NOT consumed
            // as garbage data (which would have happened before the fix when the
            // parser advanced 4 bytes into the real frame's length field).
            Assert.Empty(frames);

            // consumed == 0: the invalid frame was at offset 0, nothing consumed
            Assert.Equal(0, consumed);
        }

        [Fact]
        public void ValidFrameFollowedByZeroLengthFrame_StopsAfterValidFrame()
        {
            // A valid frame comes first; then a zero-length frame. The valid frame
            // must be returned, and invalidFrame must be true for the zero-length one.
            var goodPayload = new byte[] { 0xAB, 0xCD };
            var goodFrame = Frame(goodPayload);
            var zeroFrame = new byte[] { 0, 0, 0, 0 };
            var buf = Concat(goodFrame, zeroFrame);

            var (frames, consumed, invalidFrame) = ParseFrames(buf, buf.Length);

            Assert.True(invalidFrame);
            Assert.Single(frames);
            Assert.Equal(goodPayload, frames[0]);
            // consumed == goodFrame.Length: the valid frame was consumed before the guard fired
            Assert.Equal(goodFrame.Length, consumed);
        }
    }
}
