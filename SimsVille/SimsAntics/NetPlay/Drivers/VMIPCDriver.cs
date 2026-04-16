/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * IPC VM driver. Accepts VMNetCommands over a Unix domain socket so that an
 * external process (e.g. a Go sidecar bridging campfire) can inject commands
 * into the SimAntics VM without the old GonzoNet multiplayer stack.
 *
 * Activated when FREESIMS_IPC=1 is set in the environment.
 *
 * Wire protocol (both directions):
 *   [4-byte little-endian payload length][payload bytes]
 *
 * Inbound payloads are VMNetCommand binary (same as VMNetCommand.Deserialize).
 * Outbound payloads (tick acks) are:
 *   [4-byte LE tick_id][4-byte LE command_count][8-byte LE random_seed]
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using FSO.SimAntics.Diagnostics;
using FSO.SimAntics.NetPlay.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using GonzoNet;

namespace FSO.SimAntics.NetPlay.Drivers
{
    public class VMIPCDriver : VMNetDriver
    {
        private const string SocketPath = "/tmp/freesims-ipc.sock";
        private const int AckPayloadSize = 16; // 4 + 4 + 8

        private readonly Socket _listener;
        private Socket _client;
        private readonly List<VMNetCommand> _queued = new List<VMNetCommand>();

        // Receive buffer for partial reads
        private byte[] _recvBuf = new byte[8192];
        private int _recvPos;

        private uint _tickId;
        private bool _disposed;

        public VMIPCDriver()
        {
            // Remove stale socket file
            if (File.Exists(SocketPath))
                File.Delete(SocketPath);

            _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            _listener.Listen(1);
            _listener.Blocking = false;

            Console.WriteLine($"[VMIPCDriver] Listening on {SocketPath}");
        }

        public override void SendCommand(VMNetCommandBodyAbstract cmd)
        {
            _queued.Add(new VMNetCommand(cmd));
        }

        public override bool Tick(VM vm)
        {
            AcceptPendingClient();
            var ipcCommands = DrainInboundCommands();

            // Execute IPC commands with avatar-aware lookup — in local mode
            // GetObjectByPersist may return a game object that shadows the avatar.
            foreach (var cmd in ipcCommands)
                ExecuteIPCCommand(vm, cmd);

            // Execute local (UI) commands and tick the VM via the base driver path
            var commands = new List<VMNetCommand>(_queued);
            _queued.Clear();
            var tick = new VMNetTick
            {
                TickID = _tickId,
                Commands = commands,
                RandomSeed = vm.Context.RandomSeed,
                ImmediateMode = false,
            };
            InternalTick(vm, tick);
            SendAck(_tickId, (uint)(commands.Count + ipcCommands.Count), vm.Context.RandomSeed);

            // Emit perception events for externally-controlled idle Sims
            PerceptionEmitter.EmitPerceptions(vm, SendPerceptionFrame);

            _tickId++;

            return true;
        }

        public override string GetUserIP(uint uid) => "ipc";

        public override void CloseNet()
        {
            if (_disposed) return;
            _disposed = true;

            try { _client?.Shutdown(SocketShutdown.Both); } catch { }
            try { _client?.Close(); } catch { }
            try { _listener?.Close(); } catch { }

            if (File.Exists(SocketPath))
            {
                try { File.Delete(SocketPath); } catch { }
            }

            Console.WriteLine("[VMIPCDriver] Closed.");
        }

        public override void OnPacket(NetworkClient client, ProcessedPacket packet) { }

        // --- private helpers ---

        private void AcceptPendingClient()
        {
            if (_client != null) return;

            try
            {
                if (_listener.Poll(0, SelectMode.SelectRead))
                {
                    _client = _listener.Accept();
                    _client.Blocking = false;
                    _recvPos = 0;
                    Console.WriteLine("[VMIPCDriver] Client connected.");
                }
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[VMIPCDriver] Accept error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reads all complete length-prefixed frames from the client socket.
        /// Handles partial reads by accumulating into _recvBuf.
        /// </summary>
        private List<VMNetCommand> DrainInboundCommands()
        {
            var result = new List<VMNetCommand>();
            if (_client == null) return result;

            // Read whatever is available into the buffer
            try
            {
                while (true)
                {
                    if (!_client.Poll(0, SelectMode.SelectRead))
                        break;

                    // Ensure room in buffer
                    if (_recvPos >= _recvBuf.Length)
                        GrowBuffer();

                    int n = _client.Receive(_recvBuf, _recvPos, _recvBuf.Length - _recvPos, SocketFlags.None);
                    if (n == 0)
                    {
                        // Client disconnected
                        Console.WriteLine("[VMIPCDriver] Client disconnected.");
                        DropClient();
                        return result;
                    }
                    _recvPos += n;
                }
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode != SocketError.WouldBlock)
                {
                    Console.Error.WriteLine($"[VMIPCDriver] Recv error: {ex.Message}");
                    DropClient();
                    return result;
                }
            }

            // Parse complete frames
            int offset = 0;
            while (offset + 4 <= _recvPos)
            {
                int payloadLen = BitConverter.ToInt32(_recvBuf, offset);
                if (payloadLen < 0 || payloadLen > 1_000_000)
                {
                    Console.Error.WriteLine($"[VMIPCDriver] Invalid frame length {payloadLen}, dropping client.");
                    DropClient();
                    return result;
                }

                if (offset + 4 + payloadLen > _recvPos)
                    break; // incomplete frame, wait for more data

                try
                {
                    using (var ms = new MemoryStream(_recvBuf, offset + 4, payloadLen, writable: false))
                    using (var reader = new BinaryReader(ms))
                    {
                        var cmd = new VMNetCommand();
                        cmd.Deserialize(reader);
                        result.Add(cmd);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[VMIPCDriver] Deserialize error: {ex.Message}");
                    // Skip this frame, continue with next
                }

                offset += 4 + payloadLen;
            }

            // Compact buffer: shift unconsumed bytes to front
            if (offset > 0)
            {
                int remaining = _recvPos - offset;
                if (remaining > 0)
                    Buffer.BlockCopy(_recvBuf, offset, _recvBuf, 0, remaining);
                _recvPos = remaining;
            }

            return result;
        }

        private void SendAck(uint tickId, uint commandCount, ulong randomSeed)
        {
            if (_client == null) return;

            try
            {
                // Build ack: [4-byte LE length][4-byte tick_id][4-byte cmd_count][8-byte seed]
                var buf = new byte[4 + AckPayloadSize];
                BitConverter.TryWriteBytes(new Span<byte>(buf, 0, 4), AckPayloadSize);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 4, 4), tickId);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 8, 4), commandCount);
                BitConverter.TryWriteBytes(new Span<byte>(buf, 12, 8), randomSeed);

                int sent = 0;
                while (sent < buf.Length)
                {
                    int n = _client.Send(buf, sent, buf.Length - sent, SocketFlags.None);
                    if (n <= 0) { DropClient(); return; }
                    sent += n;
                }
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[VMIPCDriver] Ack send error: {ex.Message}");
                DropClient();
            }
        }

        /// <summary>
        /// Sends a pre-framed perception event (already includes 4-byte LE length prefix)
        /// to the connected IPC client.
        /// </summary>
        private void SendPerceptionFrame(byte[] frame)
        {
            if (_client == null) return;

            try
            {
                int sent = 0;
                while (sent < frame.Length)
                {
                    int n = _client.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                    if (n <= 0) { DropClient(); return; }
                    sent += n;
                }
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[VMIPCDriver] Perception send error: {ex.Message}");
                DropClient();
            }
        }

        /// <summary>
        /// Emits a JSON response frame to the IPC client with the given request_id and status.
        /// Format: {"type":"response","request_id":"...","status":"ok"|"error","payload":{}}
        /// The frame is length-prefixed (same as all other outbound frames).
        /// </summary>
        private void SendResponseFrame(string requestId, string status)
        {
            if (_client == null) return;

            try
            {
                // Manually build the JSON to avoid a dependency on System.Text.Json or Newtonsoft.
                // requestId and status are controlled values — no embedded quotes to escape.
                var escaped = requestId.Replace("\\", "\\\\").Replace("\"", "\\\"");
                var json = $"{{\"type\":\"response\",\"request_id\":\"{escaped}\",\"status\":\"{status}\",\"payload\":{{}}}}";
                var jsonBytes = Encoding.UTF8.GetBytes(json);

                var frame = new byte[4 + jsonBytes.Length];
                BitConverter.TryWriteBytes(new Span<byte>(frame, 0, 4), jsonBytes.Length);
                Buffer.BlockCopy(jsonBytes, 0, frame, 4, jsonBytes.Length);

                int sent = 0;
                while (sent < frame.Length)
                {
                    int n = _client.Send(frame, sent, frame.Length - sent, SocketFlags.None);
                    if (n <= 0) { DropClient(); return; }
                    sent += n;
                }
            }
            catch (SocketException ex)
            {
                Console.Error.WriteLine($"[VMIPCDriver] Response send error: {ex.Message}");
                DropClient();
            }
        }

        private void DropClient()
        {
            try { _client?.Shutdown(SocketShutdown.Both); } catch { }
            try { _client?.Close(); } catch { }
            _client = null;
            _recvPos = 0;
        }

        /// <summary>
        /// IPC commands use ActorUID as PersistID, but in local mode game objects
        /// may shadow an avatar's PersistID. Temporarily reassign the avatar's
        /// PersistID to a collision-free value so internal command lookups
        /// (e.g. VMNetGotoCmd.Execute) find the avatar, not a game object.
        /// </summary>
        private void ExecuteIPCCommand(VM vm, VMNetCommand cmd)
        {
            var uid = cmd.Command.ActorUID;
            var avatar = vm.Entities.OfType<VMAvatar>().FirstOrDefault(a => a.PersistID == uid);
            bool success;

            if (avatar == null)
            {
                success = cmd.Command.Execute(vm, null);
            }
            else
            {
                var firstMatch = vm.Entities.FirstOrDefault(e => e.PersistID == uid);
                if (firstMatch == avatar)
                {
                    success = cmd.Command.Execute(vm, avatar);
                }
                else
                {
                    uint tempId = uint.MaxValue - (uint)avatar.ObjectID;
                    uint originalId = avatar.PersistID;
                    avatar.PersistID = tempId;
                    cmd.Command.ActorUID = tempId;
                    try
                    {
                        success = cmd.Command.Execute(vm, avatar);
                    }
                    finally
                    {
                        avatar.PersistID = originalId;
                    }
                }
            }

            // If the command carries a correlation ID, emit a response frame so
            // the external agent can correlate result to request.
            var requestId = cmd.Command.RequestID;
            if (requestId != null)
            {
                SendResponseFrame(requestId, success ? "ok" : "error");
            }
        }

        private void GrowBuffer()
        {
            var newBuf = new byte[_recvBuf.Length * 2];
            Buffer.BlockCopy(_recvBuf, 0, newBuf, 0, _recvPos);
            _recvBuf = newBuf;
        }
    }
}
