/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/. 
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace FSO.SimAntics.NetPlay.Model
{
    public abstract class VMNetCommandBodyAbstract : VMSerializable
    {
        public uint ActorUID;
        public bool FromNet = false;

        /// <summary>
        /// Optional correlation ID set by external agents (e.g. the Go sidecar).
        /// When non-null, the VM driver echoes a response frame back to the sender
        /// with a matching request_id so the caller can correlate commands to outcomes.
        ///
        /// Wire format: appended LAST in the body, after all type-specific fields:
        ///   [byte hasRequestID]  — 0 = absent, 1 = present
        ///   [if present: 7-bit-length-prefixed UTF-8 string]
        ///
        /// Null when not supplied (byte 0 at tail).
        /// </summary>
        public string RequestID = null;

        public virtual bool AcceptFromClient
        {
            get { return true; }
        }

        public virtual bool Execute(VM vm) { return true; }

        public virtual bool Execute(VM vm, VMAvatar caller) { return Execute(vm); }

        public virtual void Deserialize(BinaryReader reader) {
            FromNet = true;
            ActorUID = reader.ReadUInt32();
        }

        /// <summary>
        /// Reads the optional RequestID tail from the stream.
        /// Call this AFTER reading all type-specific fields in subclass Deserialize.
        /// </summary>
        protected void DeserializeRequestID(BinaryReader reader)
        {
            try
            {
                if (reader.BaseStream.Position < reader.BaseStream.Length)
                {
                    byte hasId = reader.ReadByte();
                    if (hasId == 1)
                        RequestID = reader.ReadString(); // BinaryReader.ReadString = 7-bit-length-prefixed
                }
            }
            catch (EndOfStreamException) { /* tail absent — old client */ }
        }

        public virtual void SerializeInto(BinaryWriter writer) {
            writer.Write(ActorUID);
        }

        /// <summary>
        /// Writes the optional RequestID tail.
        /// Call this AFTER writing all type-specific fields in subclass SerializeInto.
        /// </summary>
        protected void SerializeRequestID(BinaryWriter writer)
        {
            if (RequestID != null)
            {
                writer.Write((byte)1);
                writer.Write(RequestID); // BinaryWriter.Write(string) = 7-bit-length-prefixed
            }
            else
            {
                writer.Write((byte)0);
            }
        }

        //verifies commands sent by clients before running and forwarding them.
        //if "Verify" returns true, the server runs the command and it is sent to clients
        //this prevents forwarding bogus requests - though some verifications are performed as the command is sequenced.
        //certain commands like "StateSyncCommand" cannot be forwarded from clients.

        //note - that returning false from here will only prevent the command from being forwarded IMMEDIATELY.
        //Architecture and Buy Object commands perform asynchronous transactions and then resend their command on success later.

        //verify is not run on clients.
        public virtual bool Verify(VM vm, VMAvatar caller)
        {
            return true;
        }
    }
}
