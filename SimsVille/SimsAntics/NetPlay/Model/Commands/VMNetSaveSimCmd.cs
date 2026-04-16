/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetSaveSimCmd (reeims-eb9)
 *
 * Save a Sim's full marshalled state (VMAvatarMarshal) to a file under
 * Content/Saves/. Allows persisting sim state across lot reloads / process
 * restarts so agents can re-spawn a specific Sim on demand.
 *
 * Wire format (after [VMCommandType byte = 43]):
 *   [ActorUID: 4 bytes LE]            — PersistID of the Sim to save
 *   [hasRequestID: 1 byte]            — 0 or 1
 *   [if 1: 7-bit-length-prefixed UTF-8 requestID]
 *   [filename: 7-bit-length-prefixed UTF-8 string]
 *
 * Layout per item spec:
 *   [43][uid:4][hasReq=1][7bit-len+reqID][7bit-len+filename]
 *
 * Response payload (JSON):
 *   On success:  {"status":"ok","filename":"<path>","bytes_written":N}
 *   On error:    {"status":"error","error":"<short_key>"}
 *
 * Error keys:
 *   sim_not_found, empty_filename, absolute_path_not_allowed,
 *   tilde_path_not_allowed, separator_in_filename, parent_traversal_not_allowed,
 *   write_failed
 *
 * File format: a VMAvatarMarshal binary blob written by the standard
 * BinaryWriter-based SerializeInto path. Version byte (int32 LE) is the
 * marshalling version (currently 2 to match VMAvatarMarshal's default).
 */

using System;
using System.IO;
using FSO.SimAntics.Diagnostics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetSaveSimCmd : VMNetCommandBodyAbstract
    {
        /// <summary>
        /// Save file version. Incremented when the save format changes so the
        /// load path can refuse mismatched blobs cleanly.
        /// </summary>
        public const int SaveVersion = 2;

        public string Filename = "";

        public override bool Execute(VM vm, VMAvatar caller)
        {
            var reqId = RequestID;
            RequestID = null;

            var driver = vm.Driver as VMIPCDriver;
            if (driver == null)
                return false;

            // Validate filename FIRST — even if no avatar is found, we still want
            // to report the more specific "path safety" error before "sim_not_found".
            // But path errors are usually more actionable, so report them first
            // so the agent can fix and retry without guessing.
            string resolvedPath;
            var pathErr = SimSaveStore.ValidateFilename(Filename, out resolvedPath);
            if (pathErr != SimSaveStore.PathError.Ok)
            {
                if (reqId != null)
                    driver.SendSaveSimResponse(reqId, "error", SimSaveStore.ErrorKey(pathErr), null, 0);
                return false;
            }

            // Find the avatar by ActorUID (PersistID).
            VMAvatar avatar = null;
            foreach (var ent in vm.Entities)
            {
                var a = ent as VMAvatar;
                if (a != null && a.PersistID == ActorUID)
                {
                    avatar = a;
                    break;
                }
            }

            if (avatar == null)
            {
                if (reqId != null)
                    driver.SendSaveSimResponse(reqId, "error", "sim_not_found", null, 0);
                return false;
            }

            // Build the marshal via the avatar's own Save() path.
            VMAvatarMarshal marshal;
            try
            {
                marshal = avatar.Save();
            }
            catch (Exception ex)
            {
                if (reqId != null)
                    driver.SendSaveSimResponse(reqId, "error", "marshal_failed:" + ex.GetType().Name, null, 0);
                return false;
            }

            int bytesWritten;
            try
            {
                SimSaveStore.EnsureBaseDir();
                using (var ms = new MemoryStream())
                using (var writer = new BinaryWriter(ms))
                {
                    // Leading magic + version so the load path can sanity-check.
                    writer.Write((uint)0x5333494D); // "MIS3" LE  (Marshalled SIm Snapshot v3)
                    writer.Write(SaveVersion);

                    marshal.SerializeInto(writer);
                    writer.Flush();

                    var data = ms.ToArray();
                    File.WriteAllBytes(resolvedPath, data);
                    bytesWritten = data.Length;
                }
            }
            catch (Exception ex)
            {
                if (reqId != null)
                    driver.SendSaveSimResponse(reqId, "error", "write_failed:" + ex.GetType().Name, null, 0);
                return false;
            }

            if (reqId != null)
                driver.SendSaveSimResponse(reqId, "ok", null, resolvedPath, bytesWritten);

            return true;
        }

        #region Serialization

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);   // ActorUID
            SerializeRequestID(writer);   // [hasReq][optional reqID]
            writer.Write(Filename ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);     // ActorUID + FromNet
            // RequestID before Filename per spec wire format:
            //   [uid:4][hasReq=1][7bit-len+reqID][7bit-len+filename]
            try
            {
                byte hasId = reader.ReadByte();
                if (hasId == 1)
                    RequestID = reader.ReadString();
            }
            catch (EndOfStreamException) { /* malformed — bail */ }

            try
            {
                Filename = reader.ReadString();
            }
            catch (EndOfStreamException) { Filename = ""; }
        }

        #endregion
    }
}
