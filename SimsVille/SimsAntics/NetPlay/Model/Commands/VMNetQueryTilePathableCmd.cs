/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetQueryTilePathableCmd (reeims-d3c)
 *
 * Query whether a tile is pathable (can a Sim stand there?).
 * This is a best-effort static check — it does NOT invoke the full A* router.
 *
 * Heuristic checks (in order):
 *   1. Out-of-bounds or invalid level → pathable=false, reason="out_of_bounds"
 *   2. Solid wall segment on this tile (TopLeft or TopRight, non-door) → reason="wall"
 *   3. An entity's footprint covers the tile center (x*16+8, y*16+8) at this level
 *      → reason="blocked_by_object"
 *   4. Otherwise → pathable=true, reason="clear"
 *
 * Wire format (after [VMCommandType byte = 42]):
 *   [ActorUID: 4 bytes LE]
 *   [hasRequestID: byte]           — 0 or 1
 *   [if 1: 7-bit-length-prefixed UTF-8 request ID]
 *   [x: int16 LE]                  — tile x (0-based)
 *   [y: int16 LE]                  — tile y (0-based)
 *   [level: byte]                  — floor level (1-based; 1 = ground)
 *
 * Response payload (JSON):
 *   {"pathable":bool,"reason":"clear"|"wall"|"blocked_by_object"|"out_of_bounds"}
 *
 * VMCommandType byte: 42
 */

using System.IO;
using System.Linq;
using System.Text;
using FSO.LotView.Model;
using FSO.SimAntics.NetPlay.Drivers;
using Microsoft.Xna.Framework;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetQueryTilePathableCmd : VMNetCommandBodyAbstract
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

            var json = BuildPathableJson(vm, X, Y, Level);
            driver.SendTilePathableResponse(reqId, json);
            return true;
        }

        private static string BuildPathableJson(VM vm, short x, short y, byte level)
        {
            var arch = vm.Context.Architecture;
            int lvl = level - 1; // 0-based index

            // Check 1: bounds
            if (arch == null
                || lvl < 0 || lvl >= arch.Stories
                || x < 0 || x >= arch.Width
                || y < 0 || y >= arch.Height)
            {
                return "{\"pathable\":false,\"reason\":\"out_of_bounds\"}";
            }

            // Check 2: solid wall on this tile
            var tile = arch.Walls[lvl][y * arch.Width + x];
            if (tile.TopLeftSolid || tile.TopRightSolid)
            {
                return "{\"pathable\":false,\"reason\":\"wall\"}";
            }

            // Check 3: entity footprint covers tile center
            // Tile center in 1/16 units: (x*16+8, y*16+8)
            var center = new Point(x * 16 + 8, y * 16 + 8);
            foreach (var ent in vm.Entities)
            {
                if (ent.Position.Level != level) continue;
                if (ent is VMAvatar) continue; // avatars are not static obstacles
                var fp = ent.Footprint;
                if (fp != null && fp.HardContains(center))
                {
                    return "{\"pathable\":false,\"reason\":\"blocked_by_object\"}";
                }
            }

            return "{\"pathable\":true,\"reason\":\"clear\"}";
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
