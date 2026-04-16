/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetQueryFloorAtCmd (reeims-d3c)
 *
 * Query floor tile state at a specific tile. External agents use this to
 * inspect the build-mode floor grid without driving the full renderer.
 *
 * Wire format (after [VMCommandType byte = 41]):
 *   [ActorUID: 4 bytes LE]
 *   [hasRequestID: byte]           — 0 or 1
 *   [if 1: 7-bit-length-prefixed UTF-8 request ID]
 *   [x: int16 LE]                  — tile x (0-based)
 *   [y: int16 LE]                  — tile y (0-based)
 *   [level: byte]                  — floor level (1-based; 1 = ground)
 *
 * Response payload (JSON):
 *   {"has_floor":bool,"pattern_id":N}
 *
 * has_floor is true when pattern_id != 0.
 * Out-of-bounds tiles return has_floor=false, pattern_id=0.
 *
 * VMCommandType byte: 41
 */

using System.IO;
using System.Text;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetQueryFloorAtCmd : VMNetCommandBodyAbstract
    {
        public short X;
        public short Y;
        public byte Level;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            // Capture and clear RequestID to prevent duplicate response from ExecuteIPCCommand.
            var reqId = RequestID;
            RequestID = null;

            var driver = vm.Driver as VMIPCDriver;
            if (driver == null)
                return false;

            if (reqId == null)
                return true;

            var arch = vm.Context.Architecture;
            var json = BuildFloorJson(arch, X, Y, Level);
            driver.SendFloorAtResponse(reqId, json);
            return true;
        }

        private static string BuildFloorJson(VMArchitecture arch, short x, short y, byte level)
        {
            int lvl = level - 1; // 0-based index
            if (arch == null
                || lvl < 0 || lvl >= arch.Stories
                || x < 0 || x >= arch.Width
                || y < 0 || y >= arch.Height)
            {
                return "{\"has_floor\":false,\"pattern_id\":0}";
            }

            var tile = arch.Floors[lvl][y * arch.Width + x];
            bool hasFloor = tile.Pattern != 0;

            var sb = new StringBuilder(48);
            sb.Append("{\"has_floor\":");
            sb.Append(hasFloor ? "true" : "false");
            sb.Append(",\"pattern_id\":");
            sb.Append(tile.Pattern);
            sb.Append('}');
            return sb.ToString();
        }

        #region Serialization

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);   // writes ActorUID
            SerializeRequestID(writer);   // [hasRequestID][optional requestID]
            writer.Write(X);
            writer.Write(Y);
            writer.Write(Level);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);     // reads ActorUID + sets FromNet
            DeserializeRequestID(reader); // reads optional RequestID tail
            X = reader.ReadInt16();
            Y = reader.ReadInt16();
            Level = reader.ReadByte();
        }

        #endregion
    }
}
