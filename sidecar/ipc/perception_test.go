/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

// Unit tests for the Perception struct (reeims-2ca).
//
// These tests verify that the Funds field is correctly parsed from JSON
// emitted by the game engine. The done condition for reeims-2ca requires
// that a perception event JSON containing "funds":N deserializes to
// Perception.Funds == N.

package ipc

import (
	"encoding/json"
	"testing"
)

func TestPerception_FundsDeserialized(t *testing.T) {
	// Sample JSON matching the format emitted by PerceptionEmitter.BuildPerception.
	raw := []byte(`{
		"type": "perception",
		"persist_id": 1,
		"sim_id": 42,
		"name": "Daisy",
		"funds": 12345,
		"motives": {"hunger":0,"comfort":0,"energy":0,"hygiene":0,"bladder":0,"room":0,"social":0,"fun":0,"mood":0},
		"position": {"x":10,"y":10,"level":1},
		"rotation": 0.0,
		"current_animation": "",
		"action_queue": [],
		"nearby_objects": []
	}`)

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
