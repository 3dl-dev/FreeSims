/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;

namespace FSO.SimAntics.Diagnostics
{
    /// <summary>
    /// Tracks which Sims are externally controlled (by LLM agents via IPC).
    /// Keyed by PersistID. Thread-safe for access from VM tick thread.
    /// </summary>
    public static class ExternalControllerRegistry
    {
        private static readonly HashSet<uint> _controlled = new HashSet<uint>();
        private static bool _allControlled; // when FREESIMS_IPC_CONTROL_ALL=1
        private static bool _initialized;

        public static void EnsureInitialized()
        {
            if (_initialized) return;
            _initialized = true;
            _allControlled = Environment.GetEnvironmentVariable("FREESIMS_IPC_CONTROL_ALL") == "1";
            Console.WriteLine($"[ExternalControllerRegistry] init: all={_allControlled}");
        }

        public static bool IsControlled(uint persistId)
        {
            EnsureInitialized();
            return _allControlled || _controlled.Contains(persistId);
        }

        public static void Register(uint persistId) { _controlled.Add(persistId); }
        public static void Unregister(uint persistId) { _controlled.Remove(persistId); }
    }
}
