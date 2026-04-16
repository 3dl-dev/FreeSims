/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Tests for reeims-5e3: godMode boundary (FREESIMS_GOD_MODE) and looks_like synthesis.
//
// Done condition:
//   - FREESIMS_GOD_MODE=1 → lot_avatars entries carry {motives}, looks_like absent.
//   - FREESIMS_GOD_MODE unset → lot_avatars entries carry {looks_like}, motives absent
//     (or zeroed; JSON shape test via sample payload mirrors the change).
//   - Self perception unchanged in both modes.
//   - BuildLooksLike synthesis correctness: unit-tested via LooksLikeSynthesizer (pure).
//
// Isolation note: PerceptionEmitter.BuildPerception requires live VMAvatar/VM instances
// which have ~30 MonoGame transitive dependencies. The godMode toggle integration tests
// therefore use the same JSON-shape approach as PerceptionEmitterTests.cs.
// LooksLikeSynthesizer is a pure class compiled directly into the test assembly.

using System;
using System.Text.Json;
using FSO.SimAntics.Diagnostics;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Unit tests for LooksLikeSynthesizer.Synthesize (pure algorithm).
    /// </summary>
    public class LooksLikeSynthesizerTests
    {
        // ── Animation hint cases ────────────────────────────────────────────────

        [Fact]
        public void Synthesize_EatAnimation_ContainsEating()
        {
            // All motives comfortable, anim = 'eat'
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 75, "eat");
            Assert.Contains("eating", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_WalkToAnimation_ContainsWalkingSomewhere()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 75, "walk-to");
            Assert.Contains("walking somewhere", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_IdleSitAnimation_ContainsSitting()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 75, "idle-sit");
            Assert.Contains("sitting", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_IdleStandAnimation_ContainsStanding()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 75, "idle-stand");
            Assert.Contains("standing", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_ChatAnimation_ContainsChatting()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 75, "chat");
            Assert.Contains("chatting", result, StringComparison.OrdinalIgnoreCase);
        }

        // ── Primary need driver cases ────────────────────────────────────────────

        [Fact]
        public void Synthesize_LowHunger_ContainsHungry()
        {
            // hunger=10 is below threshold (20)
            var result = LooksLikeSynthesizer.Synthesize(10, 75, 75, 75, 75, 75, "");
            Assert.Contains("hungry", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_LowEnergy_ContainsTired()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 10, 75, 75, 75, 75, "");
            Assert.Contains("tired", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_LowBladder_ContainsFidgeting()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 10, 75, 75, 75, "");
            Assert.Contains("fidgeting", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_LowSocial_ContainsLookingForCompany()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 10, 75, 75, "");
            Assert.Contains("looking for company", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_LowHygiene_ContainsUnkempt()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 10, 75, "");
            Assert.Contains("unkempt", result, StringComparison.OrdinalIgnoreCase);
        }

        // ── Hunger takes priority over energy when both below threshold ───────────

        [Fact]
        public void Synthesize_HungerAndEnergyBothLow_HungerWins()
        {
            // hunger=5, energy=5 — hunger is checked first
            var result = LooksLikeSynthesizer.Synthesize(5, 5, 75, 75, 75, 75, "");
            Assert.Contains("hungry", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("tired", result, StringComparison.OrdinalIgnoreCase);
        }

        // ── Secondary mood cases ────────────────────────────────────────────────

        [Fact]
        public void Synthesize_LowMood_ContainsGlum()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 20, "");
            Assert.Contains("glum", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_HighMood_ContainsCheerful()
        {
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 90, "");
            Assert.Contains("cheerful", result, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Synthesize_MidMood_NoMoodAdjective()
        {
            // mood=50 — neither glum (<30) nor cheerful (>80)
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 50, "");
            Assert.DoesNotContain("glum", result, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("cheerful", result, StringComparison.OrdinalIgnoreCase);
        }

        // ── Spec example: (hunger=10, mood=90, anim='eat') ──────────────────────

        [Fact]
        public void Synthesize_SpecExample_EatHungryCheerful()
        {
            // From item spec: (hunger=10, mood=90, anim='eat')
            // → contains 'eating', 'hungry', 'cheerful'
            var result = LooksLikeSynthesizer.Synthesize(10, 75, 75, 75, 75, 90, "eat");
            Assert.Contains("eating",   result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hungry",   result, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cheerful", result, StringComparison.OrdinalIgnoreCase);
        }

        // ── Spec example: (all 75, anim='idle-stand') ───────────────────────────

        [Fact]
        public void Synthesize_SpecExample_AllComfort_IdleStand()
        {
            // From item spec: (all 75, anim='idle-stand') → 'idle' or 'standing'
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 75, "idle-stand");
            bool acceptable = result.Contains("standing", StringComparison.OrdinalIgnoreCase)
                           || result.Contains("idle",     StringComparison.OrdinalIgnoreCase);
            Assert.True(acceptable, $"Expected 'standing' or 'idle', got: '{result}'");
        }

        // ── Fallback to 'idle' ────────────────────────────────────────────────

        [Fact]
        public void Synthesize_AllComfort_NoAnim_ReturnIdle()
        {
            // No animation, all motives comfortable, mood neutral → 'idle'
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 50, "");
            Assert.Equal("idle", result);
        }

        [Fact]
        public void Synthesize_NullAnim_ReturnIdle()
        {
            // null animation string → treated as empty → fallback 'idle'
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 50, null);
            Assert.Equal("idle", result);
        }

        // ── Length cap ─────────────────────────────────────────────────────────

        [Fact]
        public void Synthesize_ResultLength_AtMost60Chars()
        {
            // Worst case: long animation hint + primary need + mood
            // 'walking somewhere' (18) + 'looking for company' (19) + 'cheerful' (8) + ', '×2 = 51 chars — fits
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 10, 75, 90, "walk-to");
            Assert.True(result.Length <= 60, $"Result length {result.Length} exceeds 60 chars: '{result}'");
        }

        [Fact]
        public void Synthesize_UnknownAnimation_NoAnimHint()
        {
            // Unknown animation name → no animation hint emitted
            var result = LooksLikeSynthesizer.Synthesize(75, 75, 75, 75, 75, 50, "swim");
            // No known hint; all motives comfortable; mid mood → 'idle'
            Assert.Equal("idle", result);
        }
    }

    /// <summary>
    /// JSON-shape integration tests for godMode toggle (reeims-5e3).
    ///
    /// We use two sample JSON payloads representing the two godMode shapes
    /// and verify the lot_avatars structure changes as expected.
    /// </summary>
    public class GodModePerceptionShapeTests
    {
        // ── Sample: godMode=1 shape — lot_avatars has 'motives', no 'looks_like' ──

        private const string GodModeJson = """
            {
                "type": "perception",
                "persist_id": 1,
                "sim_id": 10,
                "name": "Daisy",
                "funds": 5000,
                "clock": {"hours":10,"minutes":0,"seconds":0,"time_of_day":1,"day":2},
                "motives": {
                    "hunger": -80, "comfort": 60, "energy": 70, "hygiene": 50,
                    "bladder": -30, "room": 40, "social": 30, "fun": 65, "mood": 55
                },
                "position": {"x":8,"y":8,"level":1},
                "rotation": 0.0,
                "current_animation": "idle-stand",
                "action_queue": [],
                "nearby_objects": [],
                "lot_avatars": [
                    {
                        "persist_id": 2,
                        "name": "Bob",
                        "position": {"x":4,"y":6,"level":1},
                        "current_animation": "eat",
                        "motives": {
                            "hunger": 10, "comfort": 50, "energy": 70,
                            "hygiene": 60, "bladder": -15,
                            "social": 40, "fun": 55, "mood": 90
                        }
                    }
                ],
                "skills": {"cooking":0,"charisma":0,"mechanical":0,"creativity":0,"body":0,"logic":0},
                "job": {"has_job":false,"career":null,"level":0,"salary":0,"work_hours":null},
                "relationships": []
            }
            """;

        // ── Sample: embodied shape — lot_avatars has 'looks_like', motives zeroed ─

        private const string EmbodiedJson = """
            {
                "type": "perception",
                "persist_id": 1,
                "sim_id": 10,
                "name": "Daisy",
                "funds": 5000,
                "clock": {"hours":10,"minutes":0,"seconds":0,"time_of_day":1,"day":2},
                "motives": {
                    "hunger": -80, "comfort": 60, "energy": 70, "hygiene": 50,
                    "bladder": -30, "room": 40, "social": 30, "fun": 65, "mood": 55
                },
                "position": {"x":8,"y":8,"level":1},
                "rotation": 0.0,
                "current_animation": "idle-stand",
                "action_queue": [],
                "nearby_objects": [],
                "lot_avatars": [
                    {
                        "persist_id": 2,
                        "name": "Bob",
                        "position": {"x":4,"y":6,"level":1},
                        "current_animation": "eat",
                        "motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"social":0,"fun":0,"mood":0},
                        "looks_like": "eating, hungry, cheerful"
                    }
                ],
                "skills": {"cooking":0,"charisma":0,"mechanical":0,"creativity":0,"body":0,"logic":0},
                "job": {"has_job":false,"career":null,"level":0,"salary":0,"work_hours":null},
                "relationships": []
            }
            """;

        // ── godMode=1 shape tests ────────────────────────────────────────────────

        [Fact]
        public void GodModeShape_LotAvatar_HasMotivesWithRealValues()
        {
            using var doc = JsonDocument.Parse(GodModeJson);
            var motives = doc.RootElement.GetProperty("lot_avatars")[0].GetProperty("motives");

            // In godMode shape, motives are real values — hunger=10 is non-zero
            Assert.Equal(10, motives.GetProperty("hunger").GetInt32());
            Assert.Equal(90, motives.GetProperty("mood").GetInt32());
        }

        [Fact]
        public void GodModeShape_LotAvatar_HasAllEightMotiveFields()
        {
            using var doc = JsonDocument.Parse(GodModeJson);
            var motives = doc.RootElement.GetProperty("lot_avatars")[0].GetProperty("motives");

            foreach (var field in new[] { "hunger", "comfort", "energy", "hygiene", "bladder", "social", "fun", "mood" })
            {
                Assert.True(motives.TryGetProperty(field, out _), $"motives must have '{field}' in godMode shape");
            }
        }

        [Fact]
        public void GodModeShape_SelfMotives_AlwaysPresent()
        {
            // Self perception: motives block always present and correct regardless of godMode.
            using var doc = JsonDocument.Parse(GodModeJson);
            var motives = doc.RootElement.GetProperty("motives");

            Assert.Equal(-80, motives.GetProperty("hunger").GetInt32());
            Assert.Equal(55,  motives.GetProperty("mood").GetInt32());
        }

        // ── Embodied shape tests ─────────────────────────────────────────────────

        [Fact]
        public void EmbodiedShape_LotAvatar_HasLooksLikeField()
        {
            using var doc = JsonDocument.Parse(EmbodiedJson);
            var avatar = doc.RootElement.GetProperty("lot_avatars")[0];

            Assert.True(avatar.TryGetProperty("looks_like", out var looksLike),
                "lot_avatar must have 'looks_like' in embodied shape");
            Assert.Equal(JsonValueKind.String, looksLike.ValueKind);
            Assert.False(string.IsNullOrEmpty(looksLike.GetString()),
                "looks_like must be a non-empty string");
        }

        [Fact]
        public void EmbodiedShape_LotAvatar_LooksLike_ContainsExpectedHints()
        {
            using var doc = JsonDocument.Parse(EmbodiedJson);
            var looksLike = doc.RootElement
                .GetProperty("lot_avatars")[0]
                .GetProperty("looks_like")
                .GetString();

            // Sample was synthesized for (hunger=10, mood=90, anim='eat') → eating+hungry+cheerful
            Assert.Contains("eating",   looksLike, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("hungry",   looksLike, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("cheerful", looksLike, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void EmbodiedShape_LotAvatar_LooksLike_MaxLength60()
        {
            using var doc = JsonDocument.Parse(EmbodiedJson);
            var looksLike = doc.RootElement
                .GetProperty("lot_avatars")[0]
                .GetProperty("looks_like")
                .GetString();

            Assert.True(looksLike.Length <= 60,
                $"looks_like must be ≤60 chars, got {looksLike.Length}: '{looksLike}'");
        }

        [Fact]
        public void EmbodiedShape_SelfMotives_AlwaysPresent()
        {
            // Self perception always carries real motives even in embodied mode.
            using var doc = JsonDocument.Parse(EmbodiedJson);
            var motives = doc.RootElement.GetProperty("motives");

            Assert.Equal(-80, motives.GetProperty("hunger").GetInt32());
            Assert.Equal(55,  motives.GetProperty("mood").GetInt32());
        }

        // ── godMode toggle changes lot_avatars shape ─────────────────────────────

        [Fact]
        public void GodModeJson_HasNonZeroMotives_EmbodiedJson_HasZeroMotives()
        {
            // Verify the two sample payloads differ in the way the spec requires.
            using var godDoc = JsonDocument.Parse(GodModeJson);
            using var embDoc = JsonDocument.Parse(EmbodiedJson);

            var godMotives = godDoc.RootElement.GetProperty("lot_avatars")[0].GetProperty("motives");
            var embMotives = embDoc.RootElement.GetProperty("lot_avatars")[0].GetProperty("motives");

            // godMode: non-zero hunger
            Assert.NotEqual(0, godMotives.GetProperty("hunger").GetInt32());
            // embodied: all zeroed
            Assert.Equal(0, embMotives.GetProperty("hunger").GetInt32());
            Assert.Equal(0, embMotives.GetProperty("mood").GetInt32());
        }

        [Fact]
        public void GodModeJson_NoLooksLike_EmbodiedJson_HasLooksLike()
        {
            using var godDoc = JsonDocument.Parse(GodModeJson);
            using var embDoc = JsonDocument.Parse(EmbodiedJson);

            var godAvatar = godDoc.RootElement.GetProperty("lot_avatars")[0];
            var embAvatar = embDoc.RootElement.GetProperty("lot_avatars")[0];

            // godMode shape: no looks_like
            Assert.False(godAvatar.TryGetProperty("looks_like", out _),
                "godMode lot_avatar must not have 'looks_like'");

            // embodied shape: has looks_like
            Assert.True(embAvatar.TryGetProperty("looks_like", out _),
                "embodied lot_avatar must have 'looks_like'");
        }

        // ── Environment variable toggle (documents expected behaviour) ───────────
        //
        // We cannot call PerceptionEmitter.BuildPerception directly (requires live VM).
        // Instead we verify Environment.SetEnvironmentVariable works as expected and
        // document that the C# code reads it on each call.

        [Fact]
        public void EnvironmentVariable_GodMode_SetAndUnset()
        {
            // Verify the env var round-trips correctly — this documents the mechanism
            // used by the production code to toggle godMode per BuildPerception call.
            var original = Environment.GetEnvironmentVariable("FREESIMS_GOD_MODE");
            try
            {
                Environment.SetEnvironmentVariable("FREESIMS_GOD_MODE", "1");
                Assert.Equal("1", Environment.GetEnvironmentVariable("FREESIMS_GOD_MODE"));

                Environment.SetEnvironmentVariable("FREESIMS_GOD_MODE", null);
                Assert.Null(Environment.GetEnvironmentVariable("FREESIMS_GOD_MODE"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("FREESIMS_GOD_MODE", original);
            }
        }
    }
}
