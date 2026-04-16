/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetQueryInventoryCmd (reeims-2ec)
 *
 * On-demand query for a Sim's current inventory contents. An external agent
 * sends this command and receives a JSON response frame with the current
 * vm.MyInventory list for the specified actor.
 *
 * Wire format (after [VMCommandType byte]):
 *   [ActorUID: 4 bytes LE]       — PersistID of the Sim whose inventory is requested
 *   [hasRequestID: byte]         — 1 if RequestID follows, else 0
 *   [if 1: 7-bit-length-prefixed UTF-8 request ID]
 *
 * Response payload (JSON):
 *   {"type":"response","request_id":"...","status":"ok",
 *    "payload":{"inventory":[{"object_pid":N,"guid":N,"name":"...","value":N,"inventory_index":N}...]}}
 *
 * VMCommandType byte: 39 (next free after QuerySimState=38)
 */

using System.IO;
using System.Text;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetQueryInventoryCmd : VMNetCommandBodyAbstract
    {
        public override bool Execute(VM vm, VMAvatar caller)
        {
            // Capture and clear RequestID to prevent duplicate response from ExecuteIPCCommand.
            var reqId = RequestID;
            RequestID = null;

            var driver = vm.Driver as VMIPCDriver;
            if (driver == null)
                return false; // only meaningful under IPC driver

            // Build JSON inventory array from vm.MyInventory.
            var sb = new StringBuilder();
            sb.Append("{\"inventory\":[");
            var inv = vm.MyInventory;
            for (int i = 0; i < inv.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var item = inv[i];
                var escapedName = (item.Name ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
                sb.Append("{\"object_pid\":");
                sb.Append(item.ObjectPID);
                sb.Append(",\"guid\":");
                sb.Append(item.GUID);
                sb.Append(",\"name\":\"");
                sb.Append(escapedName);
                sb.Append("\",\"value\":");
                sb.Append(item.Value);
                sb.Append(",\"inventory_index\":");
                sb.Append(i);
                sb.Append('}');
            }
            sb.Append("]}");

            if (reqId != null)
                driver.SendInventoryResponse(reqId, "ok", sb.ToString());

            return true;
        }

        #region Serialization

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);   // writes ActorUID
            SerializeRequestID(writer);   // [hasRequestID][optional requestID]
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);     // reads ActorUID + sets FromNet
            DeserializeRequestID(reader); // reads optional RequestID tail
        }

        #endregion
    }
}
