/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/. 
 */

using FSO.Files.Formats.IFF.Chunks;
using FSO.SimAntics.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FSO.SimAntics.NetPlay.Model.Commands
{
    public class VMNetInteractionCmd : VMNetCommandBodyAbstract
    {
        public ushort Interaction;
        public short CalleeID;
        public short Param0;
        public byte Preempt; // 1 = cancel current action, push new one at Maximum priority with Leapfrog

        public override bool Execute(VM vm)
        {
            VMEntity callee = vm.GetObjectById(CalleeID);
            VMEntity caller = vm.Entities.FirstOrDefault(x => x.PersistID == ActorUID);
            if (callee == null || caller == null) return false;
            if ((caller.Thread?.Queue?.Count ?? 0) >= VMThread.MAX_USER_ACTIONS) return false;

            if (Preempt == 1 && caller.Thread.Queue.Count > 0)
                caller.Thread.CancelAction(caller.Thread.Queue[0].UID);

            var action = callee.GetAction(Interaction, caller, vm.Context, new short[] { Param0, 0, 0, 0 });
            if (action == null) return false;
            if (Preempt == 1)
            {
                action.Priority = (short)VMQueuePriority.Maximum;
                action.Flags |= TTABFlags.Leapfrog;
            }
            caller.Thread.EnqueueAction(action);
            return true;
        }

        #region VMSerializable Members

        public override void SerializeInto(BinaryWriter writer)
        {
            base.SerializeInto(writer);
            writer.Write(Interaction);
            writer.Write(CalleeID);
            writer.Write(Param0);
            writer.Write(Preempt);
            SerializeRequestID(writer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            base.Deserialize(reader);
            Interaction = reader.ReadUInt16();
            CalleeID = reader.ReadInt16();
            Param0 = reader.ReadInt16();
            // Backward compatible: default Preempt=0 if not present
            if (reader.BaseStream.Position < reader.BaseStream.Length)
                Preempt = reader.ReadByte();
            DeserializeRequestID(reader);
        }

        #endregion
    }
}
