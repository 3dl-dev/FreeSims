/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using FSO.LotView.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Diagnostics
{
    /// <summary>
    /// Emits perception events through the IPC socket when externally-controlled
    /// Sims go idle. Called from VMIPCDriver.Tick() after InternalTick.
    /// </summary>
    public static class PerceptionEmitter
    {
        private static readonly Dictionary<uint, int> _lastEmitTick = new();
        private const int MinTicksBetweenEmits = 30; // ~1 second at 30 ticks/sec
        private static int _tickCounter;

        /// <summary>
        /// Check all externally-controlled Sims; emit a perception frame for each
        /// that is currently idle and hasn't been emitted recently.
        /// </summary>
        public static void EmitPerceptions(VM vm, Action<byte[]> sendFrame)
        {
            ExternalControllerRegistry.EnsureInitialized();
            _tickCounter++;

            foreach (var entity in vm.Entities)
            {
                if (entity is not VMAvatar avatar) continue;
                if (!ExternalControllerRegistry.IsControlled(avatar.PersistID)) continue;

                // Only emit when the Sim is idle (queue empty or only idle-priority actions)
                bool isIdle = avatar.Thread.Queue.Count == 0 ||
                    avatar.Thread.Queue.All(a => a.Priority <= (short)VMQueuePriority.Idle);
                if (!isIdle) continue;

                // Rate limit per Sim
                if (_lastEmitTick.TryGetValue(avatar.PersistID, out int lastTick) &&
                    _tickCounter - lastTick < MinTicksBetweenEmits)
                    continue;

                _lastEmitTick[avatar.PersistID] = _tickCounter;

                // Build perception JSON
                var perception = BuildPerception(vm, avatar);
                var json = JsonSerializer.SerializeToUtf8Bytes(perception);

                // Frame it: [4-byte LE length][json bytes]
                var frame = new byte[4 + json.Length];
                BitConverter.TryWriteBytes(frame.AsSpan(0, 4), json.Length);
                json.CopyTo(frame, 4);

                sendFrame(frame);
            }
        }

        private static object BuildPerception(VM vm, VMAvatar avatar)
        {
            // Nearby objects with their available interactions (pie menus)
            var nearbyObjects = new List<object>();
            foreach (var entity in vm.Entities)
            {
                if (entity == avatar) continue;
                if (entity is VMAvatar) continue; // skip other avatars for now
                if (entity.Position == LotTilePos.OUT_OF_WORLD) continue;

                var dist = LotTilePos.Distance(avatar.Position, entity.Position);
                if (dist > 400) continue; // ~25 tiles — covers most of a lot

                // Get pie menu (available interactions)
                var pieMenu = entity.GetPieMenu(vm, avatar, false);
                if (pieMenu == null || pieMenu.Count == 0) continue;

                nearbyObjects.Add(new
                {
                    object_id = (int)entity.ObjectID,
                    name = entity.ToString(),
                    position = new { x = (int)entity.Position.x, y = (int)entity.Position.y, level = (int)entity.Position.Level },
                    distance = dist,
                    interactions = pieMenu.Select(p => new
                    {
                        id = (int)p.ID,
                        name = p.Name
                    }).ToList()
                });
            }

            return new
            {
                type = "perception",
                persist_id = avatar.PersistID,
                sim_id = (int)avatar.ObjectID,
                name = avatar.ToString(),
                funds = (int)avatar.TSOState.Budget.Value,
                motives = new
                {
                    hunger = avatar.GetMotiveData(VMMotive.Hunger),
                    comfort = avatar.GetMotiveData(VMMotive.Comfort),
                    energy = avatar.GetMotiveData(VMMotive.Energy),
                    hygiene = avatar.GetMotiveData(VMMotive.Hygiene),
                    bladder = avatar.GetMotiveData(VMMotive.Bladder),
                    room = avatar.GetMotiveData(VMMotive.Room),
                    social = avatar.GetMotiveData(VMMotive.Social),
                    fun = avatar.GetMotiveData(VMMotive.Fun),
                    mood = avatar.GetMotiveData(VMMotive.Mood),
                },
                position = new { x = (int)avatar.Position.x, y = (int)avatar.Position.y, level = (int)avatar.Position.Level },
                rotation = avatar.RadianDirection,
                current_animation = avatar.CurrentAnimationState?.Anim?.Name ?? "",
                action_queue = avatar.Thread.Queue.Select(a => new
                {
                    name = a.Name ?? "",
                    target = a.Callee?.ToString() ?? "",
                    priority = (int)a.Priority,
                }).ToList(),
                nearby_objects = nearbyObjects,
            };
        }
    }
}
