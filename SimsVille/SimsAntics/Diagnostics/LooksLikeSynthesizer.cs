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
    /// Pure helper: synthesises a short observable description of another Sim's
    /// visible state from their raw motives + current animation name.
    ///
    /// Used by PerceptionEmitter in embodied mode (FREESIMS_GOD_MODE unset) to
    /// give LLM agents a plausible read of other Sims without exposing raw motive
    /// values (reeims-5e3 Knowledge Boundary spec).
    ///
    /// This file has no dependencies on MonoGame or FreeSO types so it can be
    /// compiled directly into the test project (SimsVille.Tests.csproj) for unit
    /// testing without the full game runtime.
    /// </summary>
    public static class LooksLikeSynthesizer
    {
        /// <summary>
        /// Synthesize a ≤60-char observable description.
        ///
        /// Algorithm:
        ///   1. Animation hint: 'eat'→'eating', 'walk-to'→'walking somewhere',
        ///      'idle-sit'→'sitting', 'idle-stand'→'standing', 'chat'→'chatting'.
        ///   2. Primary driver — lowest motive below threshold (hunger→'hungry',
        ///      energy→'tired', bladder→'fidgeting', social→'looking for company',
        ///      hygiene→'unkempt'); first match wins (hunger checked first).
        ///   3. Secondary mood: mood&lt;30→'glum', mood&gt;80→'cheerful'.
        /// Comma-joined; fallback 'idle' if empty.
        /// </summary>
        public static string Synthesize(short hunger, short energy, short bladder,
            short social, short hygiene, short mood, string animationName)
        {
            // Step 1: animation hint
            string animHint = "";
            if (!string.IsNullOrEmpty(animationName))
            {
                if (animationName.StartsWith("eat",      StringComparison.OrdinalIgnoreCase)) animHint = "eating";
                else if (animationName.StartsWith("walk-to",   StringComparison.OrdinalIgnoreCase)) animHint = "walking somewhere";
                else if (animationName.StartsWith("idle-sit",  StringComparison.OrdinalIgnoreCase)) animHint = "sitting";
                else if (animationName.StartsWith("idle-stand",StringComparison.OrdinalIgnoreCase)) animHint = "standing";
                else if (animationName.StartsWith("chat",      StringComparison.OrdinalIgnoreCase)) animHint = "chatting";
            }

            // Step 2: primary need driver — first motive below threshold
            string need = "";
            if      (hunger  < 20) need = "hungry";
            else if (energy  < 20) need = "tired";
            else if (bladder < 20) need = "fidgeting";
            else if (social  < 20) need = "looking for company";
            else if (hygiene < 20) need = "unkempt";

            // Step 3: secondary mood adjective
            string moodHint = mood < 30 ? "glum" : (mood > 80 ? "cheerful" : "");

            var parts = new List<string>(3);
            if (!string.IsNullOrEmpty(animHint)) parts.Add(animHint);
            if (!string.IsNullOrEmpty(need))     parts.Add(need);
            if (!string.IsNullOrEmpty(moodHint)) parts.Add(moodHint);

            if (parts.Count == 0) return "idle";

            var result = string.Join(", ", parts);
            return result.Length > 60 ? result.Substring(0, 60) : result;
        }
    }
}
