/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for the Perception struct (reeims-2ca, reeims-d43, reeims-edc).
//
// These tests verify that the Funds field is correctly parsed from JSON
// emitted by the game engine (reeims-2ca), that the Clock struct is
// correctly parsed (reeims-d43), and that the Skills struct is correctly
// parsed (reeims-edc). day_of_week is absent from VMClock and must not
// appear in the emitted JSON.

package ipc

import (
	"encoding/json"
	"testing"
)

// fullPerceptionJSON is a sample payload matching PerceptionEmitter.BuildPerception output.
const fullPerceptionJSON = `{
	"type": "perception",
	"persist_id": 1,
	"sim_id": 42,
	"name": "Daisy",
	"funds": 12345,
	"clock": {"hours":14,"minutes":30,"seconds":45,"time_of_day":0,"day":3},
	"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
	"position": {"x":10,"y":10,"level":1},
	"rotation": 0.0,
	"current_animation": "",
	"action_queue": [],
	"nearby_objects": [],
	"lot_avatars": [
		{
			"persist_id": 2,
			"name": "Bob",
			"position": {"x":5,"y":7,"level":1},
			"current_animation": "idle",
			"motives": {"hunger":-50,"comfort":30,"energy":60,"hygiene":20,"bladder":-10,"social":40,"fun":55,"mood":25}
		}
	],
	"skills": {"cooking":500,"charisma":300,"mechanical":200,"creativity":750,"body":100,"logic":900},
	"job": {"has_job":true,"career":null,"level":2,"salary":0,"work_hours":null},
	"relationships": [
		{"other_persist_id":2,"other_name":"Bob","friendship":250,"family_tag":null,"is_roommate":false}
	]
}`

func TestPerception_FundsDeserialized(t *testing.T) {
	// Sample JSON matching the format emitted by PerceptionEmitter.BuildPerception.
	raw := []byte(fullPerceptionJSON)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if p.Funds != 12345 {
		t.Errorf("expected Funds=12345, got %d", p.Funds)
	}
}

func TestPerception_FundsZeroWhenAbsent(t *testing.T) {
	// JSON without a funds field — zero value expected (no AvatarState scenario).
	raw := []byte(`{
		"type": "perception",
		"persist_id": 2,
		"sim_id": 7,
		"name": "Bob",
		"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
		"position": {"x":5,"y":5,"level":1},
		"rotation": 1.5,
		"current_animation": "",
		"action_queue": [],
		"nearby_objects": []
	}`)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if p.Funds != 0 {
		t.Errorf("expected Funds=0 when field absent, got %d", p.Funds)
	}
}

func TestPerception_FundsRoundTrip(t *testing.T) {
	// Construct a Perception with known Funds and verify marshal→unmarshal is stable.
	original := Perception{
		Type:      "perception",
		PersistID: 99,
		SimID:     1,
		Name:      "Test Sim",
		Funds:     999999,
	}

	encoded, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal failed: %v", err)
	}

	var decoded Perception
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if decoded.Funds != original.Funds {
		t.Errorf("round-trip: expected Funds=%d, got %d", original.Funds, decoded.Funds)
	}
}

// ── Clock tests (reeims-d43) ──────────────────────────────────────────────────

func TestPerception_ClockDeserialized(t *testing.T) {
	raw := []byte(fullPerceptionJSON)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if p.Clock.Hours != 14 {
		t.Errorf("expected Clock.Hours=14, got %d", p.Clock.Hours)
	}
	if p.Clock.Minutes != 30 {
		t.Errorf("expected Clock.Minutes=30, got %d", p.Clock.Minutes)
	}
	if p.Clock.Seconds != 45 {
		t.Errorf("expected Clock.Seconds=45, got %d", p.Clock.Seconds)
	}
	if p.Clock.TimeOfDay != 0 {
		t.Errorf("expected Clock.TimeOfDay=0, got %d", p.Clock.TimeOfDay)
	}
	if p.Clock.Day != 3 {
		t.Errorf("expected Clock.Day=3, got %d", p.Clock.Day)
	}
}

func TestPerception_ClockZeroWhenAbsent(t *testing.T) {
	// JSON without a clock field — zero values expected.
	raw := []byte(`{
		"type": "perception",
		"persist_id": 2,
		"sim_id": 7,
		"name": "Bob",
		"funds": 0,
		"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
		"position": {"x":5,"y":5,"level":1},
		"rotation": 0.0,
		"current_animation": "",
		"action_queue": [],
		"nearby_objects": []
	}`)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if p.Clock.Hours != 0 || p.Clock.Minutes != 0 || p.Clock.Seconds != 0 ||
		p.Clock.TimeOfDay != 0 || p.Clock.Day != 0 {
		t.Errorf("expected all Clock fields zero when absent, got %+v", p.Clock)
	}
}

func TestPerception_ClockRoundTrip(t *testing.T) {
	original := Perception{
		Type:      "perception",
		PersistID: 5,
		SimID:     3,
		Name:      "Test",
		Clock:     Clock{Hours: 8, Minutes: 15, Seconds: 0, TimeOfDay: 0, Day: 7},
	}

	encoded, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal failed: %v", err)
	}

	var decoded Perception
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if decoded.Clock != original.Clock {
		t.Errorf("round-trip: expected Clock=%+v, got %+v", original.Clock, decoded.Clock)
	}
}

// ── LotAvatars tests (reeims-d37) ─────────────────────────────────────────────

func TestPerception_LotAvatars_Deserialized(t *testing.T) {
	var p Perception
	if err := json.Unmarshal([]byte(fullPerceptionJSON), &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if len(p.LotAvatars) != 1 {
		t.Fatalf("expected 1 lot_avatar, got %d", len(p.LotAvatars))
	}

	a := p.LotAvatars[0]
	if a.PersistID != 2 {
		t.Errorf("expected persist_id=2, got %d", a.PersistID)
	}
	if a.Name != "Bob" {
		t.Errorf("expected name='Bob', got %q", a.Name)
	}
	if a.Position.X != 5 || a.Position.Y != 7 || a.Position.Level != 1 {
		t.Errorf("expected position={5,7,1}, got %+v", a.Position)
	}
	if a.CurrentAnimation != "idle" {
		t.Errorf("expected current_animation='idle', got %q", a.CurrentAnimation)
	}
}

func TestPerception_LotAvatars_ZeroLengthWhenAbsent(t *testing.T) {
	// JSON without lot_avatars field — nil/empty slice expected.
	raw := []byte(`{
		"type": "perception",
		"persist_id": 1,
		"sim_id": 42,
		"name": "Daisy",
		"funds": 0,
		"clock": {"hours":0,"minutes":0,"seconds":0,"time_of_day":0,"day":0},
		"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
		"position": {"x":0,"y":0,"level":1},
		"rotation": 0.0,
		"current_animation": "",
		"action_queue": [],
		"nearby_objects": []
	}`)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if len(p.LotAvatars) != 0 {
		t.Errorf("expected empty lot_avatars when field absent, got %d entries", len(p.LotAvatars))
	}
}

// ── PathfindFailed tests (reeims-9e7) ─────────────────────────────────────────

func TestPathfindFailed_Deserialized(t *testing.T) {
	raw := []byte(`{"type":"pathfind-failed","sim_persist_id":42,"target_object_id":7,"reason":"no-path"}`)

	var pf PathfindFailed
	if err := json.Unmarshal(raw, &pf); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if pf.Type != "pathfind-failed" {
		t.Errorf("expected Type='pathfind-failed', got %q", pf.Type)
	}
	if pf.SimPersistID != 42 {
		t.Errorf("expected SimPersistID=42, got %d", pf.SimPersistID)
	}
	if pf.TargetObjectID != 7 {
		t.Errorf("expected TargetObjectID=7, got %d", pf.TargetObjectID)
	}
	if pf.Reason != "no-path" {
		t.Errorf("expected Reason='no-path', got %q", pf.Reason)
	}
}

func TestPathfindFailed_ZeroTargetObjectID(t *testing.T) {
	raw := []byte(`{"type":"pathfind-failed","sim_persist_id":10,"target_object_id":0,"reason":"no-valid-goals"}`)

	var pf PathfindFailed
	if err := json.Unmarshal(raw, &pf); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if pf.TargetObjectID != 0 {
		t.Errorf("expected TargetObjectID=0 when no target, got %d", pf.TargetObjectID)
	}
}

func TestPathfindFailed_RoundTrip(t *testing.T) {
	original := PathfindFailed{
		Type:           "pathfind-failed",
		SimPersistID:   99,
		TargetObjectID: 15,
		Reason:         "blocked",
	}

	encoded, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal failed: %v", err)
	}

	var decoded PathfindFailed
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if decoded.Type != original.Type {
		t.Errorf("round-trip Type: expected %q, got %q", original.Type, decoded.Type)
	}
	if decoded.SimPersistID != original.SimPersistID {
		t.Errorf("round-trip SimPersistID: expected %d, got %d", original.SimPersistID, decoded.SimPersistID)
	}
	if decoded.TargetObjectID != original.TargetObjectID {
		t.Errorf("round-trip TargetObjectID: expected %d, got %d", original.TargetObjectID, decoded.TargetObjectID)
	}
	if decoded.Reason != original.Reason {
		t.Errorf("round-trip Reason: expected %q, got %q", original.Reason, decoded.Reason)
	}
}

func TestPathfindFailed_AllReasonCategories(t *testing.T) {
	reasons := []string{
		"no-route", "no-path", "no-room-route", "blocked",
		"cant-sit", "cant-stand", "no-valid-goals", "locked-door",
		"interrupted", "unknown",
	}
	for _, reason := range reasons {
		t.Run(reason, func(t *testing.T) {
			pf := PathfindFailed{
				Type:           "pathfind-failed",
				SimPersistID:   1,
				TargetObjectID: 0,
				Reason:         reason,
			}
			encoded, err := json.Marshal(pf)
			if err != nil {
				t.Fatalf("marshal failed: %v", err)
			}
			var decoded PathfindFailed
			if err := json.Unmarshal(encoded, &decoded); err != nil {
				t.Fatalf("unmarshal failed: %v", err)
			}
			if decoded.Reason != reason {
				t.Errorf("expected reason=%q, got %q", reason, decoded.Reason)
			}
		})
	}
}

// ── Skills tests (reeims-edc) ─────────────────────────────────────────────────

func TestPerception_Skills_Deserialized(t *testing.T) {
	var p Perception
	if err := json.Unmarshal([]byte(fullPerceptionJSON), &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if p.Skills.Cooking != 500 {
		t.Errorf("expected Skills.Cooking=500, got %d", p.Skills.Cooking)
	}
	if p.Skills.Charisma != 300 {
		t.Errorf("expected Skills.Charisma=300, got %d", p.Skills.Charisma)
	}
	if p.Skills.Mechanical != 200 {
		t.Errorf("expected Skills.Mechanical=200, got %d", p.Skills.Mechanical)
	}
	if p.Skills.Creativity != 750 {
		t.Errorf("expected Skills.Creativity=750, got %d", p.Skills.Creativity)
	}
	if p.Skills.Body != 100 {
		t.Errorf("expected Skills.Body=100, got %d", p.Skills.Body)
	}
	if p.Skills.Logic != 900 {
		t.Errorf("expected Skills.Logic=900, got %d", p.Skills.Logic)
	}
}

func TestPerception_Skills_ZeroWhenAbsent(t *testing.T) {
	raw := []byte(`{
		"type": "perception",
		"persist_id": 1,
		"sim_id": 42,
		"name": "Daisy",
		"funds": 0,
		"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
		"position": {"x":0,"y":0,"level":1},
		"rotation": 0.0,
		"current_animation": "",
		"action_queue": [],
		"nearby_objects": []
	}`)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if p.Skills != (Skills{}) {
		t.Errorf("expected all Skills zero when field absent, got %+v", p.Skills)
	}
}

func TestPerception_Skills_RoundTrip(t *testing.T) {
	original := Perception{
		Type:      "perception",
		PersistID: 1,
		SimID:     42,
		Name:      "Daisy",
		Skills: Skills{
			Cooking:    1000,
			Charisma:   800,
			Mechanical: 600,
			Creativity: 400,
			Body:       200,
			Logic:      1000,
		},
	}

	encoded, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal failed: %v", err)
	}

	var decoded Perception
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if decoded.Skills != original.Skills {
		t.Errorf("round-trip skills: expected %+v, got %+v", original.Skills, decoded.Skills)
	}
}

// ── Job tests (reeims-930) ────────────────────────────────────────────────────

func TestPerception_Job_Deserialized(t *testing.T) {
	var p Perception
	if err := json.Unmarshal([]byte(fullPerceptionJSON), &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if !p.Job.HasJob {
		t.Errorf("expected Job.HasJob=true, got false")
	}
	if p.Job.Career != nil {
		t.Errorf("expected Job.Career=nil (known gap: TS1JobProvider not wired), got %v", p.Job.Career)
	}
	if p.Job.Level != 2 {
		t.Errorf("expected Job.Level=2, got %d", p.Job.Level)
	}
	if p.Job.Salary != 0 {
		t.Errorf("expected Job.Salary=0 (known gap: TS1JobProvider not wired), got %d", p.Job.Salary)
	}
	if p.Job.WorkHours != nil {
		t.Errorf("expected Job.WorkHours=nil (known gap: TS1JobProvider not wired), got %v", p.Job.WorkHours)
	}
}

func TestPerception_Job_HasJobFalseWhenUnemployed(t *testing.T) {
	// Verify that has_job=false is correctly parsed for an unemployed Sim.
	raw := []byte(`{
		"type": "perception",
		"persist_id": 1,
		"sim_id": 42,
		"name": "Daisy",
		"funds": 0,
		"clock": {"hours":0,"minutes":0,"seconds":0,"time_of_day":0,"day":0},
		"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
		"position": {"x":0,"y":0,"level":1},
		"rotation": 0.0,
		"current_animation": "",
		"action_queue": [],
		"nearby_objects": [],
		"skills": {"cooking":0,"charisma":0,"mechanical":0,"creativity":0,"body":0,"logic":0},
		"job": {"has_job":false,"career":null,"level":0,"salary":0,"work_hours":null}
	}`)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if p.Job.HasJob {
		t.Errorf("expected Job.HasJob=false for unemployed Sim, got true")
	}
	if p.Job.Level != 0 {
		t.Errorf("expected Job.Level=0 for unemployed Sim, got %d", p.Job.Level)
	}
}

func TestPerception_Job_RoundTrip(t *testing.T) {
	original := Perception{
		Type:      "perception",
		PersistID: 1,
		SimID:     42,
		Name:      "Daisy",
		Job: Job{
			HasJob: true,
			Career: nil,
			Level:  3,
			Salary: 0,
		},
	}

	encoded, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal failed: %v", err)
	}

	var decoded Perception
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if decoded.Job.HasJob != original.Job.HasJob {
		t.Errorf("round-trip Job.HasJob: expected %v, got %v", original.Job.HasJob, decoded.Job.HasJob)
	}
	if decoded.Job.Career != original.Job.Career {
		t.Errorf("round-trip Job.Career: expected nil, got %v", decoded.Job.Career)
	}
	if decoded.Job.Level != original.Job.Level {
		t.Errorf("round-trip Job.Level: expected %d, got %d", original.Job.Level, decoded.Job.Level)
	}
}

func TestPerception_LotAvatars_MotivesRoundTrip(t *testing.T) {
	original := Perception{
		Type:      "perception",
		PersistID: 1,
		SimID:     10,
		Name:      "Daisy",
		LotAvatars: []LotAvatar{
			{
				PersistID:        2,
				Name:             "Bob",
				Position:         Position{X: 3, Y: 4, Level: 1},
				CurrentAnimation: "eat",
				Motives: LotAvatarMotives{
					Hunger:  -100,
					Comfort: 50,
					Energy:  80,
					Hygiene: 60,
					Bladder: -20,
					Social:  30,
					Fun:     70,
					Mood:    45,
				},
			},
		},
	}

	encoded, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal failed: %v", err)
	}

	var decoded Perception
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if len(decoded.LotAvatars) != 1 {
		t.Fatalf("round-trip: expected 1 lot_avatar, got %d", len(decoded.LotAvatars))
	}

	got := decoded.LotAvatars[0]
	want := original.LotAvatars[0]
	if got.Motives != want.Motives {
		t.Errorf("round-trip motives: expected %+v, got %+v", want.Motives, got.Motives)
	}
	if got.PersistID != want.PersistID {
		t.Errorf("round-trip persist_id: expected %d, got %d", want.PersistID, got.PersistID)
	}
	if got.Name != want.Name {
		t.Errorf("round-trip name: expected %q, got %q", want.Name, got.Name)
	}
	if got.CurrentAnimation != want.CurrentAnimation {
		t.Errorf("round-trip current_animation: expected %q, got %q", want.CurrentAnimation, got.CurrentAnimation)
	}
}

// ── Relationships tests (reeims-2eb) ──────────────────────────────────────────

func TestPerception_Relationships_Deserialized(t *testing.T) {
	// Verify that a relationships array with one entry deserializes correctly.
	var p Perception
	if err := json.Unmarshal([]byte(fullPerceptionJSON), &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if len(p.Relationships) != 1 {
		t.Fatalf("expected 1 relationship, got %d", len(p.Relationships))
	}

	r := p.Relationships[0]
	if r.OtherPersistID != 2 {
		t.Errorf("expected OtherPersistID=2, got %d", r.OtherPersistID)
	}
	if r.OtherName != "Bob" {
		t.Errorf("expected OtherName='Bob', got %q", r.OtherName)
	}
	if r.Friendship != 250 {
		t.Errorf("expected Friendship=250, got %d", r.Friendship)
	}
	if r.FamilyTag != nil {
		t.Errorf("expected FamilyTag=nil (known gap: not tracked at runtime), got %v", r.FamilyTag)
	}
	if r.IsRoommate {
		t.Errorf("expected IsRoommate=false, got true")
	}
}

func TestPerception_Relationships_EmptyWhenAbsent(t *testing.T) {
	// JSON without a relationships field — empty slice expected (no interactions yet).
	raw := []byte(`{
		"type": "perception",
		"persist_id": 1,
		"sim_id": 42,
		"name": "Daisy",
		"funds": 0,
		"clock": {"hours":0,"minutes":0,"seconds":0,"time_of_day":0,"day":0},
		"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
		"position": {"x":0,"y":0,"level":1},
		"rotation": 0.0,
		"current_animation": "",
		"action_queue": [],
		"nearby_objects": []
	}`)

	var p Perception
	if err := json.Unmarshal(raw, &p); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if len(p.Relationships) != 0 {
		t.Errorf("expected empty relationships when field absent, got %d entries", len(p.Relationships))
	}
}

func TestPerception_Relationships_MultiEntryRoundTrip(t *testing.T) {
	// Construct a Perception with multiple relationships and verify marshal→unmarshal stability.
	tag := "sibling"
	original := Perception{
		Type:      "perception",
		PersistID: 1,
		SimID:     10,
		Name:      "Daisy",
		Relationships: []Relationship{
			{OtherPersistID: 2, OtherName: "Bob", Friendship: 500, FamilyTag: nil, IsRoommate: false},
			{OtherPersistID: 3, OtherName: "Alice", Friendship: -200, FamilyTag: &tag, IsRoommate: true},
			{OtherPersistID: 4, OtherName: "Charlie", Friendship: 0, FamilyTag: nil, IsRoommate: false},
		},
	}

	encoded, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("json.Marshal failed: %v", err)
	}

	var decoded Perception
	if err := json.Unmarshal(encoded, &decoded); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if len(decoded.Relationships) != 3 {
		t.Fatalf("round-trip: expected 3 relationships, got %d", len(decoded.Relationships))
	}

	for i, want := range original.Relationships {
		got := decoded.Relationships[i]
		if got.OtherPersistID != want.OtherPersistID {
			t.Errorf("[%d] OtherPersistID: want %d, got %d", i, want.OtherPersistID, got.OtherPersistID)
		}
		if got.OtherName != want.OtherName {
			t.Errorf("[%d] OtherName: want %q, got %q", i, want.OtherName, got.OtherName)
		}
		if got.Friendship != want.Friendship {
			t.Errorf("[%d] Friendship: want %d, got %d", i, want.Friendship, got.Friendship)
		}
		if got.IsRoommate != want.IsRoommate {
			t.Errorf("[%d] IsRoommate: want %v, got %v", i, want.IsRoommate, got.IsRoommate)
		}
		// FamilyTag: both nil or both point to equal strings
		if (got.FamilyTag == nil) != (want.FamilyTag == nil) {
			t.Errorf("[%d] FamilyTag nil mismatch: want %v, got %v", i, want.FamilyTag, got.FamilyTag)
		} else if got.FamilyTag != nil && *got.FamilyTag != *want.FamilyTag {
			t.Errorf("[%d] FamilyTag value: want %q, got %q", i, *want.FamilyTag, *got.FamilyTag)
		}
	}
}

// --- DialogEvent tests (reeims-9be) ---

func TestDialogEvent_JSONRoundTrip_AllFields(t *testing.T) {
	// Verify that a dialog event JSON frame can be unmarshalled into DialogEvent
	// with all required fields populated correctly.
	raw := []byte(`{"type":"dialog","dialog_id":7,"sim_persist_id":42,"title":"Hungry?","text":"You are hungry.","buttons":["Yes","No"]}`)

	var de DialogEvent
	if err := json.Unmarshal(raw, &de); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}

	if de.Type != "dialog" {
		t.Errorf("Type = %q, want dialog", de.Type)
	}
	if de.DialogID != 7 {
		t.Errorf("DialogID = %d, want 7", de.DialogID)
	}
	if de.SimPersistID != 42 {
		t.Errorf("SimPersistID = %d, want 42", de.SimPersistID)
	}
	if de.Title != "Hungry?" {
		t.Errorf("Title = %q, want Hungry?", de.Title)
	}
	if de.Text != "You are hungry." {
		t.Errorf("Text = %q, want You are hungry.", de.Text)
	}
	if len(de.Buttons) != 2 {
		t.Fatalf("len(Buttons) = %d, want 2", len(de.Buttons))
	}
	if de.Buttons[0] != "Yes" {
		t.Errorf("Buttons[0] = %q, want Yes", de.Buttons[0])
	}
	if de.Buttons[1] != "No" {
		t.Errorf("Buttons[1] = %q, want No", de.Buttons[1])
	}
}

func TestDialogEvent_JSONRoundTrip_EmptyButtons(t *testing.T) {
	raw := []byte(`{"type":"dialog","dialog_id":1,"sim_persist_id":0,"title":"Info","text":"Done.","buttons":[]}`)
	var de DialogEvent
	if err := json.Unmarshal(raw, &de); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}
	if len(de.Buttons) != 0 {
		t.Errorf("len(Buttons) = %d, want 0", len(de.Buttons))
	}
}

func TestDialogEvent_JSONRoundTrip_ThreeButtons(t *testing.T) {
	raw := []byte(`{"type":"dialog","dialog_id":2,"sim_persist_id":5,"title":"Choice","text":"Pick one.","buttons":["Yes","No","Cancel"]}`)
	var de DialogEvent
	if err := json.Unmarshal(raw, &de); err != nil {
		t.Fatalf("json.Unmarshal failed: %v", err)
	}
	if len(de.Buttons) != 3 {
		t.Fatalf("len(Buttons) = %d, want 3", len(de.Buttons))
	}
	if de.Buttons[2] != "Cancel" {
		t.Errorf("Buttons[2] = %q, want Cancel", de.Buttons[2])
	}
}

func TestDialogEvent_MarshalRoundTrip(t *testing.T) {
	// Verify marshal → unmarshal preserves all fields.
	original := DialogEvent{
		Type:         "dialog",
		DialogID:     99,
		SimPersistID: 77,
		Title:        "Say \"hello\"",
		Text:         "Line1\nLine2",
		Buttons:      []string{"Yes", "No"},
	}
	data, err := json.Marshal(original)
	if err != nil {
		t.Fatalf("marshal: %v", err)
	}
	var got DialogEvent
	if err := json.Unmarshal(data, &got); err != nil {
		t.Fatalf("unmarshal: %v", err)
	}
	if got.DialogID != original.DialogID {
		t.Errorf("DialogID = %d, want %d", got.DialogID, original.DialogID)
	}
	if got.Title != original.Title {
		t.Errorf("Title = %q, want %q", got.Title, original.Title)
	}
	if got.Text != original.Text {
		t.Errorf("Text = %q, want %q", got.Text, original.Text)
	}
}
