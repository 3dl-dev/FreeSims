/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetQuerySimStateCmd (reeims-9e0)
 *
 * On-demand query for a Sim's full perception-shape payload. An external agent
 * sends this command and receives the same JSON shape that PerceptionEmitter
 * emits when the Sim goes idle — regardless of whether the Sim is currently
 * idle or busy.
 *
 * Wire format (after [VMCommandType byte]):
 *   [ActorUID: 4 bytes LE]       — caller identity (conventionally 0 for queries)
 *   [hasRequestID: byte]         — 1 if RequestID follows, else 0
 *   [if 1: 7-bit-length-prefixed UTF-8 request ID]
 *   [sim_persist_id: uint32 LE]  — PersistID of the Sim whose state is requested
 *
 * Response is emitted via VMIPCDriver.SendQuerySimStateResponse with the full
 * perception payload. RequestID is cleared before returning so that
 * ExecuteIPCCommand does not send a duplicate frame.
 *
 * VMCommandType byte: 38 (verified: 37 is LoadLot, 38 is next free)
 */

using System.IO;
using System.Linq;
using System.Text.Json;
using FSO.SimAntics.Diagnostics;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetQuerySimStateCmd : VMNetCommandBodyAbstract
    {
        /// <summary>
        /// PersistID of the Sim whose state is requested.
        /// </summary>
        public uint SimPersistID;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            // Capture and clear RequestID to prevent duplicate response from ExecuteIPCCommand.
            var reqId = RequestID;
            RequestID = null;

            var driver = vm.Driver as VMIPCDriver;
            if (driver == null)
                return false; // only meaningful under IPC driver

            // Look up the avatar by PersistID.
            var avatar = vm.Entities.OfType<VMAvatar>().FirstOrDefault(a => a.PersistID == SimPersistID);
            if (avatar == null)
            {
                if (reqId != null)
                    driver.SendQuerySimStateResponse(reqId, "error", "sim_not_found", null);
                return false;
            }

            // Build the perception payload — same shape as PerceptionEmitter.
            var payload = PerceptionEmitter.BuildPerceptionPayload(vm, avatar);
            var payloadJson = JsonSerializer.Serialize(payload);

            if (reqId != null)
                driver.SendQuerySimStateResponse(reqId, "ok", null, payloadJson);

            return true;
        }

        #region Serialization

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);       // writes ActorUID
            SerializeRequestID(writer);        // [hasRequestID][optional requestID]
            writer.Write(SimPersistID);        // [sim_persist_id: uint32 LE]
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);          // reads ActorUID + sets FromNet
            // RequestID BEFORE sim_persist_id per wire format spec:
            //   [type=38][uid:4][flag=1][7bit-len+requestID][uint32 LE sim_persist_id]
            try
            {
                byte hasId = reader.ReadByte();
                if (hasId == 1)
                    RequestID = reader.ReadString();
            }
            catch (System.IO.EndOfStreamException) { /* malformed — bail */ }

            try
            {
                SimPersistID = reader.ReadUInt32();
            }
            catch (System.IO.EndOfStreamException) { SimPersistID = 0; }
        }

        #endregion
    }
}
