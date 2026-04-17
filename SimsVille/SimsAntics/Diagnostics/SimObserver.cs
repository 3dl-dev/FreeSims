/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model;

namespace FSO.SimAntics.Diagnostics
{
    /// <summary>
    /// Read-only observer that serializes Sim state as JSONL every N ticks.
    /// Enable via FREESIMS_OBSERVER=1 environment variable.
    /// Output: /tmp/freesims-observer.jsonl (one JSON object per avatar per sample).
    /// </summary>
    public static class SimObserver
    {
        private const int SampleInterval = 30;
        private const string OutputPath = "/tmp/freesims-observer.jsonl";

        private static bool _initialized;
        private static bool _enabled;
        private static bool _disabled; // set true on write failure — stops further attempts
        private static int _tickCounter;
        private static StreamWriter _writer;

        /// <summary>
        /// Called at the end of VM.InternalTick(). If the observer is not enabled
        /// or has been disabled due to error, this is a no-op.
        /// </summary>
        public static void OnTick(VM vm)
        {
            if (!_initialized)
            {
                _initialized = true;
                _enabled = Environment.GetEnvironmentVariable("FREESIMS_OBSERVER") == "1";
                if (_enabled)
                {
                    try
                    {
                        _writer = new StreamWriter(OutputPath, append: false) { AutoFlush = true };
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[SimObserver] Failed to open {OutputPath}: {ex.Message}");
                        _enabled = false;
                        _disabled = true;
                    }
                }
            }

            if (!_enabled || _disabled)
                return;

            _tickCounter++;
            if (_tickCounter % SampleInterval != 0)
                return;

            try
            {
                foreach (var entity in vm.Entities)
                {
                    if (entity is not VMAvatar avatar)
                        continue;

                    var animState = avatar.CurrentAnimationState;
                    string animName = null;
                    if (animState?.Anim != null)
                        animName = animState.Anim.Name;

                    var actionQueue = new List<object>();
                    if (avatar.Thread?.Queue != null)
                    {
                        foreach (var action in avatar.Thread.Queue)
                        {
                            actionQueue.Add(new
                            {
                                name = action.Name ?? "",
                                target = action.Callee?.ToString() ?? ""
                            });
                        }
                    }

                    var pos = avatar.Position;
                    var record = new
                    {
                        tick = _tickCounter,
                        sim_id = (int)avatar.ObjectID,
                        persist_id = avatar.PersistID,
                        name = avatar.ToString() ?? "",
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
                            mood = avatar.GetMotiveData(VMMotive.Mood)
                        },
                        position = new
                        {
                            x = (int)pos.x,
                            y = (int)pos.y,
                            level = (int)pos.Level
                        },
                        rotation = avatar.RadianDirection,
                        current_animation = animName ?? "",
                        action_queue = actionQueue,
                        message = avatar.Message ?? "",
                        message_timeout = avatar.MessageTimeout
                    };

                    _writer.WriteLine(JsonSerializer.Serialize(record));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[SimObserver] Write failed, disabling: {ex.Message}");
                _disabled = true;
                try { _writer?.Dispose(); } catch { }
            }
        }
    }
}
