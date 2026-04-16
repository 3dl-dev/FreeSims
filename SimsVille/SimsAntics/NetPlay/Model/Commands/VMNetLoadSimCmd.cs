/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetLoadSimCmd (reeims-eb9)
 *
 * Load a previously-saved Sim (via VMNetSaveSimCmd) from a file under
 * Content/Saves/ and spawn it on the current lot at spawn_at coordinates.
 *
 * Wire format (after [VMCommandType byte = 44]):
 *   [ActorUID: 4 bytes LE]            — ignored on load (always 0 from agent)
 *   [hasRequestID: 1 byte]            — 0 or 1
 *   [if 1: 7-bit-length-prefixed UTF-8 requestID]
 *   [filename: 7-bit-length-prefixed UTF-8 string]
 *   [x: int16 LE]
 *   [y: int16 LE]
 *   [level: byte]
 *
 * Response payload (JSON):
 *   On success:  {"status":"ok","new_persist_id":N,"position":{"x":X,"y":Y,"level":L}}
 *   On error:    {"status":"error","error":"<short_key>"}
 *
 * Error keys:
 *   empty_filename / absolute_path_not_allowed / tilde_path_not_allowed /
 *   separator_in_filename / parent_traversal_not_allowed / file_not_found /
 *   read_failed / bad_magic / bad_version / no_free_tile / spawn_failed
 *
 * If the requested spawn_at tile is occupied, the loader tries the 4 cardinal
 * neighbours (+x, -x, +y, -y). If all are occupied, responds with no_free_tile.
 *
 * After spawn, the Sim is registered with the VM and assigned a fresh PersistID
 * based on the saved PersistID (or a synthetic one if the saved ID collides).
 */

using System;
using System.Collections.Generic;
using System.IO;
using FSO.LotView.Model;
using FSO.SimAntics.Diagnostics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetLoadSimCmd : VMNetCommandBodyAbstract
    {
        public const uint SaveMagic = 0x5333494D; // "MIS3" LE — matches VMNetSaveSimCmd

        public string Filename = "";
        public short X;
        public short Y;
        public byte Level;

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var reqId = RequestID;
            RequestID = null;

            var driver = vm.Driver as VMIPCDriver;
            if (driver == null)
                return false;

            string resolvedPath;
            var pathErr = SimSaveStore.ValidateFilename(Filename, out resolvedPath);
            if (pathErr != SimSaveStore.PathError.Ok)
            {
                if (reqId != null)
                    driver.SendLoadSimResponse(reqId, "error", SimSaveStore.ErrorKey(pathErr), 0, 0, 0, 0);
                return false;
            }

            if (!File.Exists(resolvedPath))
            {
                if (reqId != null)
                    driver.SendLoadSimResponse(reqId, "error", "file_not_found", 0, 0, 0, 0);
                return false;
            }

            byte[] bytes;
            try { bytes = File.ReadAllBytes(resolvedPath); }
            catch (Exception ex)
            {
                if (reqId != null)
                    driver.SendLoadSimResponse(reqId, "error", "read_failed:" + ex.GetType().Name, 0, 0, 0, 0);
                return false;
            }

            VMAvatarMarshal marshal;
            try
            {
                using (var ms = new MemoryStream(bytes, writable: false))
                using (var reader = new BinaryReader(ms))
                {
                    uint magic = reader.ReadUInt32();
                    if (magic != SaveMagic)
                    {
                        if (reqId != null)
                            driver.SendLoadSimResponse(reqId, "error", "bad_magic", 0, 0, 0, 0);
                        return false;
                    }

                    int version = reader.ReadInt32();
                    if (version < 1 || version > VMNetSaveSimCmd.SaveVersion)
                    {
                        if (reqId != null)
                            driver.SendLoadSimResponse(reqId, "error", "bad_version", 0, 0, 0, 0);
                        return false;
                    }

                    marshal = new VMAvatarMarshal(version);
                    marshal.Deserialize(reader);
                }
            }
            catch (Exception ex)
            {
                if (reqId != null)
                    driver.SendLoadSimResponse(reqId, "error", "read_failed:" + ex.GetType().Name, 0, 0, 0, 0);
                return false;
            }

            // Create a fresh avatar instance via the template and populate from the marshal.
            VMEntity sim;
            try
            {
                var group = vm.Context.CreateObjectInstance(VMAvatar.TEMPLATE_PERSON, LotTilePos.OUT_OF_WORLD, Direction.NORTH, false);
                if (group == null || group.Objects == null || group.Objects.Count == 0)
                {
                    if (reqId != null)
                        driver.SendLoadSimResponse(reqId, "error", "spawn_failed", 0, 0, 0, 0);
                    return false;
                }
                sim = group.Objects[0];
            }
            catch (Exception ex)
            {
                if (reqId != null)
                    driver.SendLoadSimResponse(reqId, "error", "spawn_failed:" + ex.GetType().Name, 0, 0, 0, 0);
                return false;
            }

            VMAvatar avatar = (VMAvatar)sim;

            // Apply the marshal fields we can restore directly.
            try
            {
                avatar.Load(marshal);
                // avatar.LoadCrossRef refers to other entities (HandObject) which
                // don't exist in this lot yet — skip cross-ref on load. Motives,
                // person data, outfits, skin tone are all handled by Load().
                avatar.BodyOutfit = marshal.BodyOutfit;
                avatar.HeadOutfit = marshal.HeadOutfit;
                avatar.SkinTone = marshal.SkinTone;
            }
            catch (Exception ex)
            {
                if (reqId != null)
                    driver.SendLoadSimResponse(reqId, "error", "unmarshal_failed:" + ex.GetType().Name, 0, 0, 0, 0);
                return false;
            }

            // Assign PersistID: prefer saved PersistID; if it collides, synthesize one.
            uint desiredPid = marshal.PersistID;
            bool collides = false;
            foreach (var e in vm.Entities)
            {
                if (e != avatar && e.PersistID == desiredPid)
                {
                    collides = true;
                    break;
                }
            }
            if (collides || desiredPid == 0)
            {
                // Pick a fresh synthetic PID (uint.MaxValue - ObjectID is collision-free in practice).
                desiredPid = uint.MaxValue - (uint)avatar.ObjectID;
            }
            avatar.PersistID = desiredPid;

            // Find a free tile among spawn_at and 4 cardinals.
            var placed = TryPlaceWithFallback(avatar, vm, X, Y, (sbyte)Level,
                out short finalX, out short finalY, out sbyte finalLevel);
            if (!placed)
            {
                // Unplaceable: remove the avatar we just created.
                try { avatar.Delete(true, vm.Context); } catch { /* best effort */ }

                if (reqId != null)
                    driver.SendLoadSimResponse(reqId, "error", "no_free_tile", 0, 0, 0, 0);
                return false;
            }

            if (reqId != null)
                driver.SendLoadSimResponse(reqId, "ok", null, desiredPid, finalX, finalY, (byte)finalLevel);

            return true;
        }

        private static bool TryPlaceWithFallback(VMAvatar avatar, VM vm, short x, short y, sbyte level,
            out short finalX, out short finalY, out sbyte finalLevel)
        {
            // Candidate positions: requested + 4 cardinals.
            // x/y are in tile-coordinates per the spec (whole tiles); convert to
            // 1/16-tile units via FromBigTile.
            var candidates = new List<(short tx, short ty)>
            {
                (x, y),
                ((short)(x + 1), y),
                ((short)(x - 1), y),
                (x, (short)(y + 1)),
                (x, (short)(y - 1)),
            };

            foreach (var (tx, ty) in candidates)
            {
                var pos = LotTilePos.FromBigTile(tx, ty, level);
                VMPlacementResult result;
                try
                {
                    result = avatar.SetPosition(pos, Direction.WEST, vm.Context);
                }
                catch (Exception)
                {
                    continue;
                }

                if (result.Status == VMPlacementError.Success)
                {
                    finalX = tx;
                    finalY = ty;
                    finalLevel = level;
                    return true;
                }
            }

            finalX = 0; finalY = 0; finalLevel = 0;
            return false;
        }

        #region Serialization

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);   // ActorUID (ignored on load)
            SerializeRequestID(writer);   // [hasReq][optional reqID]
            writer.Write(Filename ?? "");
            writer.Write(X);
            writer.Write(Y);
            writer.Write(Level);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            try
            {
                byte hasId = reader.ReadByte();
                if (hasId == 1)
                    RequestID = reader.ReadString();
            }
            catch (EndOfStreamException) { /* malformed */ }

            try { Filename = reader.ReadString(); } catch (EndOfStreamException) { Filename = ""; }
            try { X = reader.ReadInt16(); } catch (EndOfStreamException) { X = 0; }
            try { Y = reader.ReadInt16(); } catch (EndOfStreamException) { Y = 0; }
            try { Level = reader.ReadByte(); } catch (EndOfStreamException) { Level = 1; }
        }

        #endregion
    }
}
