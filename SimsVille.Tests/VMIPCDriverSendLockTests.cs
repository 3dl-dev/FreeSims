/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Regression tests for the VMIPCDriver send-lock fix (reeims-2c1).
//
// Bug: SendAck, SendPerceptionFrame, and SendResponseFrameWithPayload each wrote
// directly to _client.Send without a shared lock.  Concurrent callers (e.g.
// vm.OnDialog firing on a thread while Tick() is mid-SendAck) could interleave
// bytes on the wire, producing frames with malformed length prefixes that cause
// the sidecar to disconnect.
//
// Fix: private readonly object _sendLock wraps the Send loop in all three methods.
//
// Isolation approach: VMIPCDriver cannot be instantiated in tests (MonoGame /
// SDL2 transitive dependencies).  We shadow the exact locking pattern that the
// fix introduces and verify the property it is meant to guarantee:
//
//   "Any number of goroutines/tasks sending length-prefixed frames through the
//    same lock see a byte stream where every length prefix exactly matches the
//    following payload — no bytes are interleaved between concurrent senders."
//
// Two test layers:
//
// 1. LockedSendHarness — a minimal shadow of the three locked Send methods that
//    writes into a MemoryStream via a shared lock.  Concurrent tasks are fired
//    and the accumulated bytes are parsed with a reader that mirrors
//    ParseFramesFromBuffer semantics.  With the lock, all frames are valid.
//    Without the lock (UnlockedSendHarness), the test is shown to detect
//    interleaving on a multi-core machine (it runs 1000 iterations and expects
//    at least one bad frame to be detected).
//
// 2. A smoke test: 50 concurrent tasks each send 20 frames through the locked
//    harness and the reader verifies zero malformed frames in 1000 total frames.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Regression for reeims-2c1: the _sendLock in VMIPCDriver prevents byte
    /// interleaving when multiple callers (Tick/OnDialog/OnPathfindFailed) send
    /// frames concurrently.
    /// </summary>
    public class VMIPCDriverSendLockTests
    {
        // -----------------------------------------------------------------------
        // Shadow harness — mirrors the three locked Send methods from VMIPCDriver.
        //
        // Uses a MemoryStream as the "wire".  Each writer holds the lock while
        // writing the full length-prefix + payload in a single contiguous Write.
        // This is equivalent to the production code where the lock wraps the
        // while (sent < buf.Length) { _client.Send(...) } loop.
        // -----------------------------------------------------------------------

        private sealed class LockedSendHarness
        {
            private readonly object _sendLock = new object();
            private readonly MemoryStream _wire = new MemoryStream();

            // Mirrors SendAck: writes a 4-byte LE length prefix + fixed 16-byte payload.
            public void SendAck(uint tickId, uint commandCount, ulong randomSeed)
            {
                const int payloadSize = 16;
                var buf = new byte[4 + payloadSize];
                BitConverter.TryWriteBytes(new Span<byte>(buf, 0, 4), payloadSize);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 4, 4), tickId);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 8, 4), commandCount);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 12, 8), randomSeed);
                lock (_sendLock)
                {
                    _wire.Write(buf, 0, buf.Length);
                }
            }

            // Mirrors SendPerceptionFrame: writes a pre-framed byte[] (already length-prefixed).
            public void SendPerceptionFrame(byte[] frame)
            {
                lock (_sendLock)
                {
                    _wire.Write(frame, 0, frame.Length);
                }
            }

            // Mirrors SendResponseFrameWithPayload: writes a JSON response frame.
            public void SendResponseFrame(string requestId, string status, string payloadJson)
            {
                var escaped = requestId.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var json = $"{{\"type\":\"response\",\"request_id\":\"{escaped}\",\"status\":\"{status}\",\"payload\":{payloadJson}}}";
                var jsonBytes = Encoding.UTF8.GetBytes(json);
                var frame = new byte[4 + jsonBytes.Length];
                BitConverter.TryWriteBytes(new Span<byte>(frame, 0, 4), jsonBytes.Length);
                Buffer.BlockCopy(jsonBytes, 0, frame, 4, jsonBytes.Length);
                lock (_sendLock)
                {
                    _wire.Write(frame, 0, frame.Length);
                }
            }

            public byte[] DrainWire()
            {
                return _wire.ToArray();
            }
        }

        // -----------------------------------------------------------------------
        // Unlocked harness — same as above but WITHOUT the lock.
        // Used in the "proof that the lock is necessary" test to verify our
        // interleave-detection logic can catch races.
        // -----------------------------------------------------------------------

        private sealed class UnlockedSendHarness
        {
            // Use a List<byte[]> + volatile int to allow unsynchronized multi-writer
            // interleaving at the byte level.  Each write appends a burst of bytes
            // with a deliberate yield in the middle to maximize interleave likelihood.
            private readonly List<byte[]> _chunks = new List<byte[]>();
            private readonly object _listLock = new object(); // just for List thread-safety

            // Simulate a Send loop that yields mid-write (worst-case race scenario).
            // The MemoryStream itself is not thread-safe, so we collect chunks and
            // concatenate at the end.
            public void SendAckInterleaved(int id, byte byteValue)
            {
                // Frame: 4-byte length (= 4) + 4 bytes of payload
                const int payloadLen = 4;
                var lenBytes = BitConverter.GetBytes(payloadLen);
                var payload = new byte[payloadLen];
                for (int i = 0; i < payloadLen; i++) payload[i] = byteValue;

                // Intentionally split the write into two halves WITHOUT a lock,
                // and yield between them to maximise interleave.
                lock (_listLock) { _chunks.Add(lenBytes); }
                Thread.Yield();
                Thread.SpinWait(100);
                lock (_listLock) { _chunks.Add(payload); }
            }

            public byte[] DrainWire()
            {
                lock (_listLock)
                {
                    int total = 0;
                    foreach (var c in _chunks) total += c.Length;
                    var buf = new byte[total];
                    int pos = 0;
                    foreach (var c in _chunks)
                    {
                        Buffer.BlockCopy(c, 0, buf, pos, c.Length);
                        pos += c.Length;
                    }
                    return buf;
                }
            }
        }

        // -----------------------------------------------------------------------
        // Frame reader: parse length-prefixed frames from a byte array.
        // Returns (validFrameCount, invalidFrameCount).
        // A frame is valid if the length prefix matches the following bytes exactly.
        // -----------------------------------------------------------------------

        private static (int valid, int invalid) ParseAllFrames(byte[] wire)
        {
            int offset = 0;
            int valid = 0;
            int invalid = 0;

            while (offset + 4 <= wire.Length)
            {
                int payloadLen = BitConverter.ToInt32(wire, offset);
                if (payloadLen <= 0 || payloadLen > 1_000_000)
                {
                    invalid++;
                    // Cannot recover alignment — stop.
                    break;
                }
                if (offset + 4 + payloadLen > wire.Length)
                {
                    // Truncated frame — counts as invalid for our purposes.
                    invalid++;
                    break;
                }
                valid++;
                offset += 4 + payloadLen;
            }

            return (valid, invalid);
        }

        // Build a pre-framed perception byte array (length-prefix + payload).
        private static byte[] BuildPerceptionFrame(byte payloadByte, int payloadLen)
        {
            var frame = new byte[4 + payloadLen];
            BitConverter.TryWriteBytes(new Span<byte>(frame, 0, 4), payloadLen);
            for (int i = 0; i < payloadLen; i++) frame[4 + i] = payloadByte;
            return frame;
        }

        // -----------------------------------------------------------------------
        // Tests
        // -----------------------------------------------------------------------

        /// <summary>
        /// Serial correctness: a single caller writes one frame of each type.
        /// All three frame types must produce valid wire bytes.
        /// </summary>
        [Fact]
        public void Serial_ThreeFrameTypes_AllParsedCorrectly()
        {
            var h = new LockedSendHarness();
            h.SendAck(tickId: 1, commandCount: 3, randomSeed: 0xDEADBEEFCAFEBABE);
            h.SendPerceptionFrame(BuildPerceptionFrame(0xAB, 8));
            h.SendResponseFrame("req-1", "ok", "{}");

            var wire = h.DrainWire();
            var (valid, invalid) = ParseAllFrames(wire);

            Assert.Equal(0, invalid);
            Assert.Equal(3, valid);
        }

        /// <summary>
        /// Regression for reeims-2c1 (lock coverage smoke test):
        /// 50 concurrent tasks each send 20 frames through the locked harness.
        /// All 1000 frames must arrive as valid length-prefixed frames — no
        /// interleaved bytes must corrupt a length prefix.
        /// </summary>
        [Fact]
        public void Concurrent_LockedSends_ProduceWellFormedFrames()
        {
            const int taskCount = 50;
            const int framesPerTask = 20;
            const int totalFrames = taskCount * framesPerTask;

            var h = new LockedSendHarness();
            var tasks = new Task[taskCount];

            for (int t = 0; t < taskCount; t++)
            {
                int taskId = t;
                tasks[t] = Task.Run(() =>
                {
                    for (int f = 0; f < framesPerTask; f++)
                    {
                        // Rotate through the three send paths to exercise all lock sites.
                        int kind = (taskId + f) % 3;
                        if (kind == 0)
                            h.SendAck((uint)taskId, (uint)f, (ulong)(taskId * 1000 + f));
                        else if (kind == 1)
                            h.SendPerceptionFrame(BuildPerceptionFrame((byte)(taskId & 0xFF), 12));
                        else
                            h.SendResponseFrame($"r{taskId}-{f}", "ok", "{\"n\":" + f + "}");
                    }
                });
            }

            Task.WaitAll(tasks);

            var wire = h.DrainWire();
            var (valid, invalid) = ParseAllFrames(wire);

            // With the lock, every frame must be well-formed.
            Assert.Equal(0, invalid);
            Assert.Equal(totalFrames, valid);
        }

        /// <summary>
        /// Demonstrates that the interleave-detection logic in ParseAllFrames CAN
        /// catch races produced by the unlocked harness.  This validates that the
        /// above test is a meaningful check — a passing "concurrent locked" result
        /// is not vacuous.
        ///
        /// We run the unlocked harness 1000 times and assert that at least one
        /// trial produced an invalid frame.  On any multi-core machine with
        /// Thread.Yield() in the writer, this fires reliably.
        ///
        /// If this test fails (all 1000 trials were clean), the machine is too
        /// slow / single-core to produce races with our harness — skip the
        /// assertion.  The locked test above remains the authoritative regression.
        /// </summary>
        [Fact]
        public void Interleave_Detection_CanCatchUnlockedRace()
        {
            // Skip assertion on single-core machines — Thread.Yield() cannot
            // produce real concurrency when there is only one hardware thread.
            if (Environment.ProcessorCount < 2)
                return;

            const int trials = 1000;
            int trialsWithInvalidFrames = 0;

            for (int trial = 0; trial < trials; trial++)
            {
                var h = new UnlockedSendHarness();
                const int writers = 8;
                var tasks = new Task[writers];

                for (int i = 0; i < writers; i++)
                {
                    byte b = (byte)(i + 1);
                    tasks[i] = Task.Run(() => h.SendAckInterleaved(i, b));
                }
                Task.WaitAll(tasks);

                var wire = h.DrainWire();
                var (_, invalid) = ParseAllFrames(wire);
                if (invalid > 0)
                    trialsWithInvalidFrames++;
            }

            // On a multi-core machine we expect many trials to show interleaving.
            // Require at least 1 out of 1000 — a very conservative bar.
            Assert.True(trialsWithInvalidFrames >= 1,
                $"Expected unlocked concurrent writes to produce at least 1 malformed frame " +
                $"across {trials} trials, but got 0.  Either the machine has no true " +
                $"parallelism (ProcessorCount={Environment.ProcessorCount}) or the harness " +
                $"needs adjustment.  The locked-send regression test remains valid.");
        }
    }
}
