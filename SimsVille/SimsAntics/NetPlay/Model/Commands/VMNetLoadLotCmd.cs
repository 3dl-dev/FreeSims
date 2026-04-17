/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

/*
 * VMNetLoadLotCmd (reeims-e8e)
 *
 * Tier 1: agents load a different lot via IPC. The external agent sends this
 * command with a house XML filename (e.g. "house2.xml"); the game engine tears
 * down the current VM/World/Blueprint and loads the new blueprint with the
 * requesting agent's Sim preserved across the reload.
 *
 * Wire format (after [VMCommandType byte]):
 *   [ActorUID: 4 bytes LE]
 *   [hasRequestID: byte]
 *   [if 1: 7-bit-length-prefixed UTF-8 request ID]
 *   [houseXml: 7-bit-length-prefixed UTF-8 string]  — filename only (e.g. "house2.xml")
 *
 * Note: the RequestID tail is emitted BEFORE the house_xml field (unlike most
 * commands which put it last). This is documented in the item spec:
 *   [type=37][uid:4][hasReq=1][7bit-len+requestID][7bit-len+house_xml]
 *
 * THREADING: Execute() runs on the VM tick thread inside VMIPCDriver.Tick.
 * We MUST NOT call CoreGameScreen.LoadLotByXmlName directly from here because
 * lot load mutates the same world that the VM is actively ticking. Instead we
 * queue a thunk via CoreGameScreen.RequestLotLoad that the UI thread picks up
 * on the next Update call. This mirrors the existing auto-load countdown hack.
 *
 * Response: a single "queued" response frame is emitted immediately. A second
 * response when the lot has actually finished loading is deferred — file a
 * follow-up item if that distinction matters to an agent.
 *
 * Sim persistence: the agent's Sim's PersistID does NOT survive lot reload in
 * this initial implementation. VMNetSimJoinCmd in InitTestLot generates a new
 * random MyUID, and the caller's previous Sim is destroyed with the old VM.
 * The agent must re-query perception / re-join with a new PersistID after the
 * reload completes. See task spec "CONSTRAINTS" — document, don't extend.
 */

using System;
using System.IO;
using FSO.SimAntics.NetPlay.Drivers;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetLoadLotCmd : VMNetCommandBodyAbstract
    {
        /// <summary>
        /// House XML filename, e.g. "house2.xml". Looked up relative to the
        /// Content/Houses directory by CoreGameScreen.LoadLotByXmlName.
        /// </summary>
        public string HouseXml = "";

        public override bool Execute(VM vm, VMAvatar caller)
        {
            // Capture RequestID before clearing — we emit our own "queued"
            // response frame so ExecuteIPCCommand doesn't emit a duplicate.
            var reqId = RequestID;
            RequestID = null;

            var driver = vm.Driver as VMIPCDriver;
            if (driver == null)
                return false; // only meaningful under IPC driver

            if (!VMHouseXmlValidator.IsValidHouseXml(HouseXml))
            {
                if (reqId != null)
                    driver.SendLoadLotResponse(reqId, "error",
                        string.IsNullOrEmpty(HouseXml) ? "empty house_xml" : "invalid house_xml path");
                return false;
            }

            // Queue the actual lot load onto the UI thread. The C# call itself
            // is thread-safe (volatile write under a lock); the actual teardown
            // and reload happens in CoreGameScreen.Update on the next tick.
            try
            {
                // Resolve CoreGameScreen.RequestLotLoad via reflection to avoid
                // a circular reference between SimAntics and the UI.Screens
                // namespace. If CoreGameScreen isn't loaded (e.g. in tests),
                // report an error.
                var coreType = Type.GetType("FSO.Client.UI.Screens.CoreGameScreen, SimsVille", throwOnError: false);
                if (coreType == null)
                {
                    if (reqId != null)
                        driver.SendLoadLotResponse(reqId, "error", "CoreGameScreen not loaded");
                    return false;
                }

                var method = coreType.GetMethod("RequestLotLoad",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (method == null)
                {
                    if (reqId != null)
                        driver.SendLoadLotResponse(reqId, "error", "RequestLotLoad missing");
                    return false;
                }

                method.Invoke(null, new object[] { HouseXml });
            }
            catch (Exception ex)
            {
                if (reqId != null)
                    driver.SendLoadLotResponse(reqId, "error", ex.Message);
                return false;
            }

            if (reqId != null)
                driver.SendLoadLotResponse(reqId, "queued", HouseXml);

            return true;
        }

        #region Serialization

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer); // writes ActorUID
            SerializeRequestID(writer); // hasRequestID + optional requestID
            writer.Write(HouseXml ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader); // reads ActorUID + sets FromNet
            // RequestID comes BEFORE HouseXml per the spec's wire format:
            //   [uid:4][hasReq=1][7bit-len+requestID][7bit-len+house_xml]
            try
            {
                byte hasId = reader.ReadByte();
                if (hasId == 1)
                    RequestID = reader.ReadString();
            }
            catch (EndOfStreamException) { /* malformed — bail */ }

            try
            {
                HouseXml = reader.ReadString();
            }
            catch (EndOfStreamException) { HouseXml = ""; }
        }

        #endregion
    }
}
