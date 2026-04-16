/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package main

import (
	"context"
	"encoding/json"
	"fmt"
	"sync"
	"testing"
)

// mockSDK records all calls to the CampfireSDK interface for test assertions.
type mockSDK struct {
	mu       sync.Mutex
	creates  []string            // descriptions passed to Create
	sends    []mockSend          // all Send calls
	nextID   int                 // monotonic counter for IDs
	createFn func(string) error // optional error injection
}

type mockSend struct {
	CampfireID string
	Payload    []byte
	Tags       []string
}

func newMockSDK() *mockSDK {
	return &mockSDK{}
}

func (m *mockSDK) Create(_ context.Context, description string) (string, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	if m.createFn != nil {
		if err := m.createFn(description); err != nil {
			return "", err
		}
	}
	m.creates = append(m.creates, description)
	m.nextID++
	return fmt.Sprintf("cf-%d", m.nextID), nil
}

func (m *mockSDK) Send(_ context.Context, campfireID string, payload []byte, tags []string) (string, error) {
	m.mu.Lock()
	defer m.mu.Unlock()
	m.sends = append(m.sends, mockSend{
		CampfireID: campfireID,
		Payload:    append([]byte(nil), payload...),
		Tags:       append([]string(nil), tags...),
	})
	m.nextID++
	return fmt.Sprintf("msg-%d", m.nextID), nil
}

func (m *mockSDK) Read(_ context.Context, _ string, _ []string) ([][]byte, error) {
	return nil, nil
}

func TestNamingInit_CreatesRootAndAPI(t *testing.T) {
	sdk := newMockSDK()
	nh := NewNamingHierarchy(sdk)

	if err := nh.Init(context.Background()); err != nil {
		t.Fatalf("Init failed: %v", err)
	}

	if len(sdk.creates) != 2 {
		t.Fatalf("expected 2 Create calls, got %d", len(sdk.creates))
	}
	if sdk.creates[0] != "freesims" {
		t.Errorf("first Create description = %q, want %q", sdk.creates[0], "freesims")
	}
	if sdk.creates[1] != "freesims.api" {
		t.Errorf("second Create description = %q, want %q", sdk.creates[1], "freesims.api")
	}
	if nh.RootID == "" {
		t.Error("RootID not set after Init")
	}
	if nh.APIID == "" {
		t.Error("APIID not set after Init")
	}
}

func TestNamingInit_PublishesAllConventions(t *testing.T) {
	sdk := newMockSDK()
	nh := NewNamingHierarchy(sdk)

	if err := nh.Init(context.Background()); err != nil {
		t.Fatalf("Init failed: %v", err)
	}

	if len(sdk.sends) != 6 {
		t.Fatalf("expected 6 Send calls (one per convention), got %d", len(sdk.sends))
	}

	// All sends should target the API campfire
	for i, s := range sdk.sends {
		if s.CampfireID != nh.APIID {
			t.Errorf("send[%d] campfireID = %q, want %q", i, s.CampfireID, nh.APIID)
		}
	}

	// Collect operation names from payloads and tags
	wantOps := map[string]bool{
		"query-lot-state": false,
		"query-pie-menu":  false,
		"query-sim-state": false,
		"query-catalog":   false,
		"sim-action":      false,
		"sim-build":       false,
	}

	for i, s := range sdk.sends {
		// Verify payload is valid JSON with an "operation" field
		var decl map[string]interface{}
		if err := json.Unmarshal(s.Payload, &decl); err != nil {
			t.Errorf("send[%d] payload is not valid JSON: %v", i, err)
			continue
		}
		op, ok := decl["operation"].(string)
		if !ok || op == "" {
			t.Errorf("send[%d] missing or empty 'operation' field", i)
			continue
		}
		if _, exists := wantOps[op]; !exists {
			t.Errorf("send[%d] unexpected operation %q", i, op)
			continue
		}
		wantOps[op] = true

		// Verify tags include convention:operation and the op-specific tag
		hasConvTag := false
		hasOpTag := false
		expectedOpTag := "freesims:" + op
		for _, tag := range s.Tags {
			if tag == "convention:operation" {
				hasConvTag = true
			}
			if tag == expectedOpTag {
				hasOpTag = true
			}
		}
		if !hasConvTag {
			t.Errorf("send[%d] missing tag 'convention:operation'", i)
		}
		if !hasOpTag {
			t.Errorf("send[%d] missing tag %q", i, expectedOpTag)
		}
	}

	for op, seen := range wantOps {
		if !seen {
			t.Errorf("convention %q was not published", op)
		}
	}
}

func TestNamingInit_EnvOverrides(t *testing.T) {
	t.Setenv("FREESIMS_CF_ROOT", "env-root-id")
	t.Setenv("FREESIMS_CF_API", "env-api-id")

	sdk := newMockSDK()
	nh := NewNamingHierarchy(sdk)

	if err := nh.Init(context.Background()); err != nil {
		t.Fatalf("Init failed: %v", err)
	}

	// No Create calls should be made when env vars are set
	if len(sdk.creates) != 0 {
		t.Errorf("expected 0 Create calls with env overrides, got %d", len(sdk.creates))
	}
	if nh.RootID != "env-root-id" {
		t.Errorf("RootID = %q, want %q", nh.RootID, "env-root-id")
	}
	if nh.APIID != "env-api-id" {
		t.Errorf("APIID = %q, want %q", nh.APIID, "env-api-id")
	}

	// Declarations should still be published
	if len(sdk.sends) != 6 {
		t.Errorf("expected 6 Send calls even with env overrides, got %d", len(sdk.sends))
	}
}

func TestNamingInit_NilSDK_NoEnv(t *testing.T) {
	nh := NewNamingHierarchy(nil)

	err := nh.Init(context.Background())
	if err == nil {
		t.Fatal("expected error with nil SDK and no env vars")
	}
	if err.Error() != "create freesims root campfire: no CampfireSDK provided" {
		t.Errorf("unexpected error: %v", err)
	}
}
