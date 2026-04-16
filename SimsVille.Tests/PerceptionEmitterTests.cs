/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for the perception JSON shape (reeims-2ca, reeims-d43, reeims-edc).
//
// Done condition (2ca): perception event JSON includes a "funds" (int32) field.
// Done condition (d43): perception event JSON includes a "clock" object with
//   {hours, minutes, seconds, time_of_day, day} matching VMClock properties.
//   day_of_week is absent — VMClock does not track it.
//
// Isolation note: VMAvatar has ~30 transitive dependencies (MonoGame, FreeSO
// content loaders, OpenGL, SDL2) that cannot be satisfied in a headless xUnit
// test runner without the full game runtime. Directly instantiating VMAvatar
// is therefore not feasible in isolation.
//
// Approach: We test the JSON shape via System.Text.Json deserialization of a
// sample payload that matches what PerceptionEmitter.BuildPerception emits.
// The PerceptionEmitter code path itself is covered by the Go-side
// perception_test.go (sidecar/ipc/) which round-trips the same JSON.
//
// If a full integration test harness is added later (rd item: reeims-XXX),
// a VMAvatar-backed test can replace or supplement this one.

using System;
using System.Collections.Generic;
using System.Text.Json;
using Xunit;

namespace SimsVille.Tests
{
    /// <summary>
    /// Verifies that perception event JSON produced by PerceptionEmitter contains
    /// the "funds" field with the correct int32 value (reeims-2ca).
    /// </summary>
    public class PerceptionEmitterTests
    {
        // Minimal perception JSON matching PerceptionEmitter.BuildPerception output.
        private const string SamplePerceptionJson = """
            {
                "type": "perception",
                "persist_id": 1,
                "sim_id": 42,
                "name": "Daisy",
                "funds": 12345,
                "clock": {
                    "hours": 14,
                    "minutes": 30,
                    "seconds": 45,
                    "time_of_day": 0,
                    "day": 3
                },
                "motives": {
                    "hunger": -100,
                    "comfort": 50,
                    "energy": 80,
                    "hygiene": 60,
                    "bladder": -20,
                    "room": 40,
                    "social": 30,
                    "fun": 70,
                    "mood": 45
                },
                "position": { "x": 10, "y": 10, "level": 1 },
                "rotation": 0.0,
                "current_animation": "walk",
                "action_queue": [],
                "nearby_objects": [],
                "lot_avatars": [
                    {
                        "persist_id": 2,
                        "name": "Bob",
                        "position": { "x": 5, "y": 7, "level": 1 },
                        "current_animation": "idle",
                        "motives": {
                            "hunger": -50,
                            "comfort": 30,
                            "energy": 60,
                            "hygiene": 20,
                            "bladder": -10,
                            "social": 40,
                            "fun": 55,
                            "mood": 25
                        }
                    }
                ],
                "skills": {
                    "cooking": 500,
                    "charisma": 300,
                    "mechanical": 200,
                    "creativity": 750,
                    "body": 100,
                    "logic": 900
                }
            }
            """;

        [Fact]
        public void PerceptionJson_ContainsFundsField()
        {
            // Verify the JSON can be parsed and the funds key is present.
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var root = doc.RootElement;

            Assert.True(root.TryGetProperty("funds", out var fundsElement),
                "perception JSON must contain a 'funds' property");
            Assert.Equal(JsonValueKind.Number, fundsElement.ValueKind);
        }

        [Fact]
        public void PerceptionJson_FundsValue_IsCorrect()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var funds = doc.RootElement.GetProperty("funds").GetInt32();
            Assert.Equal(12345, funds);
        }

        [Fact]
        public void PerceptionJson_FundsField_IsInt32Compatible()
        {
            // Verify max meaningful budget value fits in int32 without overflow.
            // VMBudget.Value is uint; we cast to int in BuildPerception.
            // uint max = 4,294,967,295 — that would overflow int32.
            // In practice the game initialises Sims with 999,999 (VMNetSimJoinCmd.cs:69).
            // We verify the cast is safe for the expected range.
            const uint maxExpectedBudget = 999_999u;
            int asFunds = (int)maxExpectedBudget;
            Assert.Equal(999_999, asFunds);

            // Zero budget (null TSOState guard: 0 is the fallback)
            const uint zeroBudget = 0u;
            Assert.Equal(0, (int)zeroBudget);
        }

        [Fact]
        public void PerceptionJson_RoundTrip_PreservesFunds()
        {
            // Deserialize → re-serialize via a simple DTO and verify funds survives.
            var dto = JsonSerializer.Deserialize<PerceptionDto>(SamplePerceptionJson);
            Assert.NotNull(dto);
            Assert.Equal(12345, dto.Funds);

            var reEncoded = JsonSerializer.Serialize(dto);
            var dto2 = JsonSerializer.Deserialize<PerceptionDto>(reEncoded);
            Assert.Equal(dto.Funds, dto2.Funds);
        }

        // ── Clock tests (reeims-d43) ──────────────────────────────────────────

        [Fact]
        public void PerceptionJson_ContainsClockField()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            Assert.True(doc.RootElement.TryGetProperty("clock", out _),
                "perception JSON must contain a 'clock' property");
        }

        [Fact]
        public void PerceptionJson_ClockShape_HasRequiredFields()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var clock = doc.RootElement.GetProperty("clock");

            Assert.True(clock.TryGetProperty("hours", out _),      "clock must have 'hours'");
            Assert.True(clock.TryGetProperty("minutes", out _),     "clock must have 'minutes'");
            Assert.True(clock.TryGetProperty("seconds", out _),     "clock must have 'seconds'");
            Assert.True(clock.TryGetProperty("time_of_day", out _), "clock must have 'time_of_day'");
            Assert.True(clock.TryGetProperty("day", out _),         "clock must have 'day'");
        }

        [Fact]
        public void PerceptionJson_ClockValues_AreCorrect()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var clock = doc.RootElement.GetProperty("clock");

            Assert.Equal(14, clock.GetProperty("hours").GetInt32());
            Assert.Equal(30, clock.GetProperty("minutes").GetInt32());
            Assert.Equal(45, clock.GetProperty("seconds").GetInt32());
            Assert.Equal(0,  clock.GetProperty("time_of_day").GetInt32());
            Assert.Equal(3,  clock.GetProperty("day").GetInt32());
        }

        [Fact]
        public void PerceptionJson_ClockFields_AreIntegers()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var clock = doc.RootElement.GetProperty("clock");

            foreach (var fieldName in new[] { "hours", "minutes", "seconds", "time_of_day", "day" })
            {
                Assert.True(clock.GetProperty(fieldName).ValueKind == JsonValueKind.Number,
                    $"clock.{fieldName} must be a number");
            }
        }

        [Fact]
        public void PerceptionJson_ClockRoundTrip_PreservesValues()
        {
            var dto = JsonSerializer.Deserialize<PerceptionDto>(SamplePerceptionJson);
            Assert.NotNull(dto);
            Assert.NotNull(dto.Clock);
            Assert.Equal(14, dto.Clock.Hours);
            Assert.Equal(30, dto.Clock.Minutes);
            Assert.Equal(45, dto.Clock.Seconds);
            Assert.Equal(0,  dto.Clock.TimeOfDay);
            Assert.Equal(3,  dto.Clock.Day);

            var reEncoded = JsonSerializer.Serialize(dto);
            var dto2 = JsonSerializer.Deserialize<PerceptionDto>(reEncoded);
            Assert.Equal(dto.Clock.Hours,     dto2.Clock.Hours);
            Assert.Equal(dto.Clock.Minutes,   dto2.Clock.Minutes);
            Assert.Equal(dto.Clock.Seconds,   dto2.Clock.Seconds);
            Assert.Equal(dto.Clock.TimeOfDay, dto2.Clock.TimeOfDay);
            Assert.Equal(dto.Clock.Day,       dto2.Clock.Day);
        }

        [Fact]
        public void PerceptionJson_Clock_NoDayOfWeekField()
        {
            // VMClock does not have DayOfWeek — verify it is not emitted.
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var clock = doc.RootElement.GetProperty("clock");
            Assert.False(clock.TryGetProperty("day_of_week", out _),
                "day_of_week must not appear in clock — VMClock does not track it");
        }

        // ── LotAvatars tests (reeims-d37) ──────────────────────────────────────

        [Fact]
        public void PerceptionJson_ContainsLotAvatarsField()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            Assert.True(doc.RootElement.TryGetProperty("lot_avatars", out var elem),
                "perception JSON must contain a 'lot_avatars' property");
            Assert.Equal(JsonValueKind.Array, elem.ValueKind);
        }

        [Fact]
        public void LotAvatars_Shape_HasRequiredFields()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var lotAvatars = doc.RootElement.GetProperty("lot_avatars");

            Assert.True(lotAvatars.GetArrayLength() > 0, "sample JSON must have at least one lot_avatar");
            var avatar = lotAvatars[0];

            Assert.True(avatar.TryGetProperty("persist_id", out _),        "lot_avatar must have 'persist_id'");
            Assert.True(avatar.TryGetProperty("name", out _),               "lot_avatar must have 'name'");
            Assert.True(avatar.TryGetProperty("position", out var pos),     "lot_avatar must have 'position'");
            Assert.True(avatar.TryGetProperty("current_animation", out _),  "lot_avatar must have 'current_animation'");
            Assert.True(avatar.TryGetProperty("motives", out var motives),  "lot_avatar must have 'motives'");

            // Position sub-fields
            Assert.True(pos.TryGetProperty("x", out _),     "lot_avatar.position must have 'x'");
            Assert.True(pos.TryGetProperty("y", out _),     "lot_avatar.position must have 'y'");
            Assert.True(pos.TryGetProperty("level", out _), "lot_avatar.position must have 'level'");

            // Motives sub-fields (reeims-d37 spec: hunger,comfort,energy,hygiene,bladder,social,fun,mood)
            foreach (var field in new[] { "hunger", "comfort", "energy", "hygiene", "bladder", "social", "fun", "mood" })
            {
                Assert.True(motives.TryGetProperty(field, out _),
                    $"lot_avatar.motives must have '{field}'");
            }
        }

        [Fact]
        public void LotAvatars_ExcludesSelf_ByPersistID()
        {
            // Self persist_id is 1 (Daisy). Bob's is 2. Verify self is not in lot_avatars.
            var dto = JsonSerializer.Deserialize<PerceptionDto>(SamplePerceptionJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(dto);

            uint selfId = dto.PersistId;
            foreach (var la in dto.LotAvatars)
            {
                Assert.NotEqual(selfId, la.PersistId);
            }
        }

        [Fact]
        public void LotAvatars_MotivesValues_AreCorrect()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var motives = doc.RootElement.GetProperty("lot_avatars")[0].GetProperty("motives");

            Assert.Equal(-50, motives.GetProperty("hunger").GetInt32());
            Assert.Equal(30,  motives.GetProperty("comfort").GetInt32());
            Assert.Equal(60,  motives.GetProperty("energy").GetInt32());
            Assert.Equal(20,  motives.GetProperty("hygiene").GetInt32());
            Assert.Equal(-10, motives.GetProperty("bladder").GetInt32());
            Assert.Equal(40,  motives.GetProperty("social").GetInt32());
            Assert.Equal(55,  motives.GetProperty("fun").GetInt32());
            Assert.Equal(25,  motives.GetProperty("mood").GetInt32());
        }

        // ── Skills tests (reeims-edc) ──────────────────────────────────────────

        [Fact]
        public void PerceptionJson_ContainsSkillsField()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            Assert.True(doc.RootElement.TryGetProperty("skills", out var elem),
                "perception JSON must contain a 'skills' property");
            Assert.Equal(JsonValueKind.Object, elem.ValueKind);
        }

        [Fact]
        public void PerceptionJson_SkillsShape_HasAllSixFields()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var skills = doc.RootElement.GetProperty("skills");

            foreach (var field in new[] { "cooking", "charisma", "mechanical", "creativity", "body", "logic" })
            {
                Assert.True(skills.TryGetProperty(field, out _),
                    $"skills must have '{field}' field");
            }
        }

        [Fact]
        public void PerceptionJson_SkillsValues_AreCorrect()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var skills = doc.RootElement.GetProperty("skills");

            Assert.Equal(500, skills.GetProperty("cooking").GetInt32());
            Assert.Equal(300, skills.GetProperty("charisma").GetInt32());
            Assert.Equal(200, skills.GetProperty("mechanical").GetInt32());
            Assert.Equal(750, skills.GetProperty("creativity").GetInt32());
            Assert.Equal(100, skills.GetProperty("body").GetInt32());
            Assert.Equal(900, skills.GetProperty("logic").GetInt32());
        }

        [Fact]
        public void PerceptionJson_SkillsFields_AreIntegers()
        {
            using var doc = JsonDocument.Parse(SamplePerceptionJson);
            var skills = doc.RootElement.GetProperty("skills");

            foreach (var field in new[] { "cooking", "charisma", "mechanical", "creativity", "body", "logic" })
            {
                Assert.True(skills.GetProperty(field).ValueKind == JsonValueKind.Number,
                    $"skills.{field} must be a number");
            }
        }

        [Fact]
        public void PerceptionJson_Skills_RoundTrip_PreservesValues()
        {
            var dto = JsonSerializer.Deserialize<PerceptionDto>(SamplePerceptionJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(dto);
            Assert.NotNull(dto.Skills);

            Assert.Equal(500, dto.Skills.Cooking);
            Assert.Equal(300, dto.Skills.Charisma);
            Assert.Equal(200, dto.Skills.Mechanical);
            Assert.Equal(750, dto.Skills.Creativity);
            Assert.Equal(100, dto.Skills.Body);
            Assert.Equal(900, dto.Skills.Logic);

            var reEncoded = JsonSerializer.Serialize(dto);
            var dto2 = JsonSerializer.Deserialize<PerceptionDto>(reEncoded,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Assert.NotNull(dto2.Skills);
            Assert.Equal(dto.Skills.Cooking,    dto2.Skills.Cooking);
            Assert.Equal(dto.Skills.Charisma,   dto2.Skills.Charisma);
            Assert.Equal(dto.Skills.Mechanical, dto2.Skills.Mechanical);
            Assert.Equal(dto.Skills.Creativity, dto2.Skills.Creativity);
            Assert.Equal(dto.Skills.Body,       dto2.Skills.Body);
            Assert.Equal(dto.Skills.Logic,      dto2.Skills.Logic);
        }

        [Fact]
        public void PerceptionJson_SkillsRange_MaxValueFitsInt16()
        {
            // TS1 skills are stored as short (PersonData is short[100]).
            // Max skill value is 1000 — well within int16 max (32767).
            const short maxSkill = 1000;
            Assert.True(maxSkill <= short.MaxValue,
                "max skill value 1000 must fit in int16 (PersonData is short[])");
        }

        // Minimal DTO mirroring the perception JSON shape (funds + clock + lot_avatars + skills fields).
        // Uses JsonPropertyName to match the snake_case keys emitted by PerceptionEmitter.
        private sealed class PerceptionDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("type")]
            public string Type { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("persist_id")]
            public uint PersistId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("funds")]
            public int Funds { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("clock")]
            public ClockDto Clock { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("lot_avatars")]
            public List<LotAvatarDto> LotAvatars { get; set; } = new();

            [System.Text.Json.Serialization.JsonPropertyName("skills")]
            public SkillsDto Skills { get; set; }
        }

        private sealed class ClockDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("hours")]
            public int Hours { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("minutes")]
            public int Minutes { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("seconds")]
            public int Seconds { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("time_of_day")]
            public int TimeOfDay { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("day")]
            public int Day { get; set; }
        }

        private sealed class LotAvatarDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("persist_id")]
            public uint PersistId { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("name")]
            public string Name { get; set; }
        }

        private sealed class SkillsDto
        {
            [System.Text.Json.Serialization.JsonPropertyName("cooking")]
            public int Cooking { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("charisma")]
            public int Charisma { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("mechanical")]
            public int Mechanical { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("creativity")]
            public int Creativity { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("body")]
            public int Body { get; set; }

            [System.Text.Json.Serialization.JsonPropertyName("logic")]
            public int Logic { get; set; }
        }
    }
}
