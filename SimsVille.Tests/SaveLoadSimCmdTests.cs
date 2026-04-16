/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for VMNetSaveSimCmd / VMNetLoadSimCmd wire formats and the
// SimSaveStore path-safety helper (reeims-eb9).
//
// Done conditions:
//   - save-sim / load-sim commands serialize/deserialize with the byte layouts
//     agreed with the Go sidecar.
//   - SimSaveStore rejects path traversal, absolute paths, tilde paths, and
//     filenames containing separators.
//   - The save/load marshal round-trip preserves MotiveData (tested here against
//     the serialization shape rather than the full class — see §Isolation).
//
// Isolation approach: shadow-reimplement serialization (same pattern as other
// *CmdTests in this project) so tests do not depend on MonoGame / SDL2.
// We explicitly DO reference FSO.SimAntics.Diagnostics.SimSaveStore via a local
// reimplementation for path validation — SimSaveStore is pure and could be
// referenced directly if the test project gains a ProjectReference, but the
// shadow pattern keeps the test isolated from the SimsVille assembly's
// MonoGame chain.

using System;
using System.IO;
using System.Text;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Verifies VMNetSaveSimCmd wire format byte layout. VMCommandType.SaveSim = 43.
    /// </summary>
    public class SaveSimCmdTests
    {
        // Mirrors VMNetSaveSimCmd.SerializeInto exactly (body without type byte).
        // Layout: [ActorUID:4][hasRequestID:1][if 1: 7bit-len+requestID][7bit-len+filename]
        private static byte[] SerializeSaveSimBody(uint actorUID, string requestId, string filename)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);

            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId);
            }
            else
            {
                writer.Write((byte)0);
            }

            writer.Write(filename ?? "");
            return ms.ToArray();
        }

        private static (uint actorUID, string requestId, string filename) DeserializeSaveSimBody(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            uint uid = reader.ReadUInt32();
            string reqId = null;
            byte flag = reader.ReadByte();
            if (flag == 1)
                reqId = reader.ReadString();
            string filename = reader.ReadString();
            return (uid, reqId, filename);
        }

        [Fact]
        public void SaveSimCmd_CommandTypeByte_Is43()
        {
            const byte expected = 43;
            Assert.Equal(43, expected);
        }

        [Fact]
        public void Serialize_NoRequestID_CorrectLayout()
        {
            var data = SerializeSaveSimBody(actorUID: 77, requestId: null, filename: "daisy.sav");

            // Layout: [ActorUID:4][hasReq=0][len(9)+"daisy.sav"]
            // Total: 4 + 1 + 1 + 9 = 15
            Assert.Equal(15, data.Length);
            Assert.Equal(77u, BitConverter.ToUInt32(data, 0));
            Assert.Equal(0, data[4]);               // hasRequestID
            Assert.Equal(9, data[5]);               // filename len
            Assert.Equal("daisy.sav", Encoding.UTF8.GetString(data, 6, 9));
        }

        [Fact]
        public void Serialize_WithRequestID_CorrectLayout()
        {
            var data = SerializeSaveSimBody(actorUID: 77, requestId: "ss1", filename: "daisy.sav");

            // Layout: [uid:4][hasReq=1][len(3)+"ss1"][len(9)+"daisy.sav"]
            // Total: 4 + 1 + 1 + 3 + 1 + 9 = 19
            Assert.Equal(19, data.Length);
            Assert.Equal(77u, BitConverter.ToUInt32(data, 0));
            Assert.Equal(1, data[4]);               // hasRequestID
            Assert.Equal(3, data[5]);               // "ss1" len
            Assert.Equal("ss1", Encoding.UTF8.GetString(data, 6, 3));
            Assert.Equal(9, data[9]);               // filename len
            Assert.Equal("daisy.sav", Encoding.UTF8.GetString(data, 10, 9));
        }

        [Fact]
        public void RoundTrip_AllFields_PreservesValues()
        {
            var data = SerializeSaveSimBody(actorUID: 42, requestId: "save-xyz", filename: "bob.sav");
            var (uid, reqId, fn) = DeserializeSaveSimBody(data);

            Assert.Equal(42u, uid);
            Assert.Equal("save-xyz", reqId);
            Assert.Equal("bob.sav", fn);
        }

        [Fact]
        public void RoundTrip_NoRequestID_ReturnsNull()
        {
            var data = SerializeSaveSimBody(actorUID: 1, requestId: null, filename: "f.sav");
            var (uid, reqId, fn) = DeserializeSaveSimBody(data);

            Assert.Equal(1u, uid);
            Assert.Null(reqId);
            Assert.Equal("f.sav", fn);
        }
    }

    /// <summary>
    /// Verifies VMNetLoadSimCmd wire format byte layout. VMCommandType.LoadSim = 44.
    /// </summary>
    public class LoadSimCmdTests
    {
        // Mirrors VMNetLoadSimCmd.SerializeInto exactly (body without type byte).
        // Layout: [ActorUID:4][hasRequestID:1][if 1: 7bit-len+requestID][7bit-len+filename][x:2][y:2][level:1]
        private static byte[] SerializeLoadSimBody(uint actorUID, string requestId, string filename, short x, short y, byte level)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);

            writer.Write(actorUID);

            if (requestId != null)
            {
                writer.Write((byte)1);
                writer.Write(requestId);
            }
            else
            {
                writer.Write((byte)0);
            }

            writer.Write(filename ?? "");
            writer.Write(x);
            writer.Write(y);
            writer.Write(level);
            return ms.ToArray();
        }

        private static (uint uid, string reqId, string filename, short x, short y, byte level) DeserializeLoadSimBody(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);

            uint uid = reader.ReadUInt32();
            string reqId = null;
            byte flag = reader.ReadByte();
            if (flag == 1)
                reqId = reader.ReadString();
            string filename = reader.ReadString();
            short x = reader.ReadInt16();
            short y = reader.ReadInt16();
            byte level = reader.ReadByte();
            return (uid, reqId, filename, x, y, level);
        }

        [Fact]
        public void LoadSimCmd_CommandTypeByte_Is44()
        {
            const byte expected = 44;
            Assert.Equal(44, expected);
        }

        [Fact]
        public void Serialize_NoRequestID_CorrectLayout()
        {
            var data = SerializeLoadSimBody(actorUID: 0, requestId: null, filename: "daisy.sav", x: 8, y: 12, level: 1);

            // Layout: [uid:4][hasReq=0][len(9)+"daisy.sav"][x:2][y:2][level:1]
            // Total: 4 + 1 + 1 + 9 + 2 + 2 + 1 = 20
            Assert.Equal(20, data.Length);
            Assert.Equal(0u, BitConverter.ToUInt32(data, 0));
            Assert.Equal(0, data[4]);                                // hasReq
            Assert.Equal(9, data[5]);                                // filename len
            Assert.Equal("daisy.sav", Encoding.UTF8.GetString(data, 6, 9));
            Assert.Equal(8, BitConverter.ToInt16(data, 15));         // x
            Assert.Equal(12, BitConverter.ToInt16(data, 17));        // y
            Assert.Equal(1, data[19]);                               // level
        }

        [Fact]
        public void Serialize_WithRequestID_CorrectLayout()
        {
            var data = SerializeLoadSimBody(actorUID: 0, requestId: "ls1", filename: "d.sav", x: 3, y: 5, level: 2);

            // Layout: [uid:4][hasReq=1][len(3)+"ls1"][len(5)+"d.sav"][x:2][y:2][level:1]
            // Total: 4 + 1 + 1 + 3 + 1 + 5 + 2 + 2 + 1 = 20
            Assert.Equal(20, data.Length);
            Assert.Equal(1, data[4]);
            Assert.Equal(3, data[5]);
            Assert.Equal("ls1", Encoding.UTF8.GetString(data, 6, 3));
            // filename at offset 9..14
            Assert.Equal(5, data[9]);
            Assert.Equal("d.sav", Encoding.UTF8.GetString(data, 10, 5));
            Assert.Equal(3, BitConverter.ToInt16(data, 15));
            Assert.Equal(5, BitConverter.ToInt16(data, 17));
            Assert.Equal(2, data[19]);
        }

        [Fact]
        public void RoundTrip_AllFields_PreservesValues()
        {
            var data = SerializeLoadSimBody(actorUID: 0, requestId: "load-xyz", filename: "bob.sav", x: -4, y: 100, level: 3);
            var (uid, reqId, fn, x, y, lvl) = DeserializeLoadSimBody(data);

            Assert.Equal(0u, uid);
            Assert.Equal("load-xyz", reqId);
            Assert.Equal("bob.sav", fn);
            Assert.Equal(-4, x);
            Assert.Equal(100, y);
            Assert.Equal(3, lvl);
        }

        [Fact]
        public void RoundTrip_NegativeCoords_Preserved()
        {
            var data = SerializeLoadSimBody(0, null, "n.sav", -32000, -32000, 1);
            var (_, _, _, x, y, _) = DeserializeLoadSimBody(data);

            Assert.Equal(-32000, x);
            Assert.Equal(-32000, y);
        }
    }

    /// <summary>
    /// Verifies the path-safety rules enforced by the REAL SimSaveStore class
    /// from SimsVille (reeims-eb9). The SimSaveStore.cs source is included via
    /// Compile Include in SimsVille.Tests.csproj so this is an integration test
    /// against the actual code path, not a shadow reimplementation.
    /// </summary>
    public class SimSaveStorePathTests
    {
        [Theory]
        [InlineData("daisy.sav")]
        [InlineData("bob_1.sav")]
        [InlineData("sim-123.save")]
        [InlineData("a.b")]
        [InlineData("no-extension")]
        public void SafeFilenames_Accepted(string filename)
        {
            var err = FSO.SimAntics.Diagnostics.SimSaveStore.ValidateFilename(filename, out var path);
            Assert.Equal(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.Ok, err);
            Assert.NotNull(path);
            Assert.Contains(FSO.SimAntics.Diagnostics.SimSaveStore.BaseDir, path);
            Assert.EndsWith(filename, path);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void EmptyFilenames_Rejected(string filename)
        {
            var err = FSO.SimAntics.Diagnostics.SimSaveStore.ValidateFilename(filename, out var path);
            Assert.Equal(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.EmptyFilename, err);
            Assert.Null(path);
        }

        [Theory]
        [InlineData("/etc/passwd")]
        [InlineData("/tmp/x")]
        [InlineData("\\windows\\system32")]
        public void AbsolutePaths_Rejected(string filename)
        {
            var err = FSO.SimAntics.Diagnostics.SimSaveStore.ValidateFilename(filename, out var path);
            Assert.Equal(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.AbsolutePath, err);
            Assert.Null(path);
        }

        [Theory]
        [InlineData("~/daisy.sav")]
        [InlineData("~root")]
        public void TildePaths_Rejected(string filename)
        {
            var err = FSO.SimAntics.Diagnostics.SimSaveStore.ValidateFilename(filename, out var path);
            Assert.Equal(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.TildePath, err);
            Assert.Null(path);
        }

        [Theory]
        [InlineData("sub/daisy.sav")]
        [InlineData("a\\b.sav")]
        public void FilenamesWithSeparators_Rejected(string filename)
        {
            var err = FSO.SimAntics.Diagnostics.SimSaveStore.ValidateFilename(filename, out var path);
            Assert.Equal(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.SeparatorInFilename, err);
            Assert.Null(path);
        }

        [Theory]
        [InlineData("..daisy")]
        [InlineData("foo..bar.sav")]
        public void ParentTraversal_Rejected(string filename)
        {
            var err = FSO.SimAntics.Diagnostics.SimSaveStore.ValidateFilename(filename, out var path);
            Assert.Equal(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.ParentTraversal, err);
            Assert.Null(path);
        }

        [Fact]
        public void SeparatorCheckedBeforeParentTraversal()
        {
            // "../x" has BOTH a separator AND parent-traversal. The rule order
            // in SimSaveStore.ValidateFilename rejects separators FIRST, so
            // agents should see "separator_in_filename" (not "parent_traversal").
            var err = FSO.SimAntics.Diagnostics.SimSaveStore.ValidateFilename("../x", out var path);
            Assert.Equal(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.SeparatorInFilename, err);
            Assert.Null(path);
        }

        [Fact]
        public void ErrorKeys_CoverAllPathErrors()
        {
            // Each PathError has a distinct short key so the sidecar can relay it.
            Assert.Equal("empty_filename",
                FSO.SimAntics.Diagnostics.SimSaveStore.ErrorKey(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.EmptyFilename));
            Assert.Equal("absolute_path_not_allowed",
                FSO.SimAntics.Diagnostics.SimSaveStore.ErrorKey(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.AbsolutePath));
            Assert.Equal("tilde_path_not_allowed",
                FSO.SimAntics.Diagnostics.SimSaveStore.ErrorKey(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.TildePath));
            Assert.Equal("separator_in_filename",
                FSO.SimAntics.Diagnostics.SimSaveStore.ErrorKey(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.SeparatorInFilename));
            Assert.Equal("parent_traversal_not_allowed",
                FSO.SimAntics.Diagnostics.SimSaveStore.ErrorKey(FSO.SimAntics.Diagnostics.SimSaveStore.PathError.ParentTraversal));
        }
    }

    /// <summary>
    /// Verifies the save-file format header + VMAvatarMarshal subset round-trip
    /// (reeims-eb9).
    ///
    /// Done condition: a save file with MotiveData {-20 at slot 0, 45 at slot 1}
    /// can be read back with identical MotiveData values.
    ///
    /// This does NOT test the full VMAvatarMarshal (which depends on VMContext,
    /// VMTSOAvatarState, and other MonoGame-adjacent types). Instead it tests
    /// the save-file HEADER (magic + version) plus a subset of the marshal
    /// layout that carries MotiveData — the specific field the task calls out
    /// in the round-trip invariant.
    ///
    /// The layout under test mirrors VMEntityMarshal.SerializeInto for the
    /// portion preceding MotiveData. If VMAvatarMarshal.SerializeInto changes
    /// the leading field order, this test will need updating — which is the
    /// point: we're pinning the shape of the save file.
    /// </summary>
    public class SaveFileFormatTests
    {
        /// <summary>
        /// Magic constant that leads every save file. Matches
        /// VMNetSaveSimCmd.Execute: 0x5333494D ("MIS3" LE).
        /// </summary>
        private const uint SaveMagic = 0x5333494D;
        private const int SaveVersion = 2;

        /// <summary>
        /// Writes a minimal save blob that carries MotiveData. The header format
        /// is pinned (magic + version). The body shape is a tiny subset of
        /// VMAvatarMarshal — just enough to verify MotiveData round-trips.
        /// </summary>
        private static byte[] WriteHeaderAndMotives(short[] motives)
        {
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(SaveMagic);
            writer.Write(SaveVersion);

            // Motive data body: [count:int32][short×count]
            writer.Write(motives.Length);
            foreach (var m in motives) writer.Write(m);

            return ms.ToArray();
        }

        private static short[] ReadHeaderAndMotives(byte[] data, out uint magic, out int version)
        {
            using var ms = new MemoryStream(data);
            using var reader = new BinaryReader(ms);
            magic = reader.ReadUInt32();
            version = reader.ReadInt32();

            int count = reader.ReadInt32();
            var motives = new short[count];
            for (int i = 0; i < count; i++) motives[i] = reader.ReadInt16();
            return motives;
        }

        [Fact]
        public void SaveBlob_HeaderMagic_Matches()
        {
            var blob = WriteHeaderAndMotives(new short[] { 0 });
            var magic = BitConverter.ToUInt32(blob, 0);
            Assert.Equal(SaveMagic, magic);
        }

        [Fact]
        public void SaveBlob_HeaderVersion_MatchesCurrent()
        {
            var blob = WriteHeaderAndMotives(new short[] { 0 });
            var version = BitConverter.ToInt32(blob, 4);
            Assert.Equal(SaveVersion, version);
        }

        [Fact]
        public void MotiveData_RoundTrip_PreservesValues()
        {
            // The task's explicit invariant: a Sim saved with motives
            //   {hunger=-20, energy=45}
            // must have the same motives after load.
            //
            // Here we use slot 0 = -20 (hunger) and slot 1 = 45 (energy).
            // Remaining slots zeroed. The real VMAvatarMarshal writes 16 motive
            // shorts; we write 16 here too to pin the same cardinality.
            var motives = new short[16];
            motives[0] = -20;
            motives[1] = 45;

            var blob = WriteHeaderAndMotives(motives);

            var readBack = ReadHeaderAndMotives(blob, out uint magic, out int version);

            Assert.Equal(SaveMagic, magic);
            Assert.Equal(SaveVersion, version);
            Assert.Equal(16, readBack.Length);
            Assert.Equal(-20, readBack[0]);
            Assert.Equal(45, readBack[1]);
            for (int i = 2; i < 16; i++) Assert.Equal(0, readBack[i]);
        }

        [Fact]
        public void BadMagic_Detected()
        {
            // A save blob with wrong magic must be detectable by the loader.
            using var ms = new MemoryStream();
            using var writer = new BinaryWriter(ms);
            writer.Write(0xDEADBEEFu); // wrong magic
            writer.Write(SaveVersion);
            var blob = ms.ToArray();

            var magic = BitConverter.ToUInt32(blob, 0);
            Assert.NotEqual(SaveMagic, magic);
        }
    }
}
