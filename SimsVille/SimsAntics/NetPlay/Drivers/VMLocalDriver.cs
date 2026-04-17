/*
 * Local (single-player) VM driver. No network, no GonzoNet, no ProtocolAbstractionLibraryD.
 * Ticks the VM in-process with commands enqueued directly from the UI. This is the
 * default driver for the modern .NET 8 port; the old VMServerDriver / VMClientDriver
 * files are excluded from the build because they depend on legacy networking assemblies
 * that reference System.ServiceModel 3.0 via baked-in type references that do not
 * resolve on net8.0.
 */

using System.Collections.Generic;
using System.IO;
using FSO.SimAntics.NetPlay.Model;
using FSO.SimAntics.NetPlay.Model.Commands;
using GonzoNet;

namespace FSO.SimAntics.NetPlay.Drivers
{
    public class VMLocalDriver : VMNetDriver
    {
        private readonly List<VMNetCommand> _queued = new List<VMNetCommand>();

        public override void SendCommand(VMNetCommandBodyAbstract cmd)
        {
            _queued.Add(new VMNetCommand(cmd));
        }

        public override bool Tick(VM vm)
        {
            var commands = new List<VMNetCommand>(_queued);
            _queued.Clear();
            var tick = new VMNetTick
            {
                Commands = commands,
                RandomSeed = vm.Context.RandomSeed,
                ImmediateMode = false,
            };
            InternalTick(vm, tick);
            return true;
        }

        public override string GetUserIP(uint uid) => "local";

        public override void CloseNet() { }

        public override void OnPacket(NetworkClient client, ProcessedPacket packet) { }
    }
}
