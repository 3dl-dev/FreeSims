/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for the Perception struct (reeims-2ca, reeims-d43).
//
// These tests verify that the Funds field is correctly parsed from JSON
// emitted by the game engine (reeims-2ca) and that the Clock struct is
// correctly parsed (reeims-d43). day_of_week is absent from VMClock and
// must not appear in the emitted JSON.

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
	"nearby_objects": []
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
