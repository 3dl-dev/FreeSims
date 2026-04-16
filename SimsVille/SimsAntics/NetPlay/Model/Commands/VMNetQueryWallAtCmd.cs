/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetQueryWallAtCmd (reeims-d3c)
 *
 * Query wall state at a specific tile. External agents use this to inspect
 * the build-mode wall grid without driving the full renderer.
 *
 * Wire format (after [VMCommandType byte = 40]):
 *   [ActorUID: 4 bytes LE]
 *   [hasRequestID: byte]           — 0 or 1
 *   [if 1: 7-bit-length-prefixed UTF-8 request ID]
 *   [x: int16 LE]                  — tile x (0-based)
 *   [y: int16 LE]                  — tile y (0-based)
 *   [level: byte]                  — floor level (1-based; 1 = ground)
 *
 * Response payload (JSON):
 *   {"has_wall":bool,"segments":N,"top_left_pattern":N,"top_right_pattern":N,
 *    "bottom_left_pattern":N,"bottom_right_pattern":N,
 *    "top_left_style":N,"top_right_style":N}
 *
 * "segments" is the raw WallSegments flags integer (bitfield).
 * has_wall is true when Segments != 0.
 *
 * Out-of-bounds tiles return has_wall=false with all fields zero.
 *
 * VMCommandType byte: 40
 */

using System.IO;
using System.Text;
using FSO.LotView.Model;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetQueryWallAtCmd : VMNetCommandBodyAbstract
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
                return true; // no point executing without a request ID

            var arch = vm.Context.Architecture;
            var json = BuildWallJson(arch, X, Y, Level);
            driver.SendWallAtResponse(reqId, json);
            return true;
        }

        private static string BuildWallJson(VMArchitecture arch, short x, short y, byte level)
        {
            // Validate bounds and level.
            int lvl = level - 1; // 0-based index into Walls array
            if (arch == null
                || lvl < 0 || lvl >= arch.Stories
                || x < 0 || x >= arch.Width
                || y < 0 || y >= arch.Height)
            {
                return "{\"has_wall\":false,\"segments\":0,"
                     + "\"top_left_pattern\":0,\"top_right_pattern\":0,"
                     + "\"bottom_left_pattern\":0,\"bottom_right_pattern\":0,"
                     + "\"top_left_style\":0,\"top_right_style\":0}";
            }

            var tile = arch.Walls[lvl][y * arch.Width + x];
            bool hasWall = tile.Segments != 0;

            var sb = new StringBuilder(128);
            sb.Append("{\"has_wall\":");
            sb.Append(hasWall ? "true" : "false");
            sb.Append(",\"segments\":");
            sb.Append((int)tile.Segments);
            sb.Append(",\"top_left_pattern\":");
            sb.Append(tile.TopLeftPattern);
            sb.Append(",\"top_right_pattern\":");
            sb.Append(tile.TopRightPattern);
            sb.Append(",\"bottom_left_pattern\":");
            sb.Append(tile.BottomLeftPattern);
            sb.Append(",\"bottom_right_pattern\":");
            sb.Append(tile.BottomRightPattern);
            sb.Append(",\"top_left_style\":");
            sb.Append(tile.TopLeftStyle);
            sb.Append(",\"top_right_style\":");
            sb.Append(tile.TopRightStyle);
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
