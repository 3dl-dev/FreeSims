/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package main

import (
	"context"
	"embed"
	"fmt"
	"log"
	"os"
	"path/filepath"
	"strings"
	"sync"
)

//go:embed conventions/*.json
var conventionFiles embed.FS

// CampfireSDK is the interface for campfire protocol operations.
// Implementations can wrap the cf CLI or the campfire Go SDK.
type CampfireSDK interface {
	Create(ctx context.Context, description string) (campfireID string, err error)
	Send(ctx context.Context, campfireID string, payload []byte, tags []string) (messageID string, err error)
	Read(ctx context.Context, campfireID string, tags []string) ([][]byte, error)
}

// NamingHierarchy manages the freesims campfire naming hierarchy:
//
//	freesims              -> root (beacon-registered)
//	freesims.api          -> convention declarations + commands
//	freesims.lots.<id>    -> per-lot state channels (created lazily)
//	freesims.sims.<id>    -> per-sim perception channels (created lazily)
type NamingHierarchy struct {
	mu     sync.Mutex
	RootID string
	APIID  string
	sdk    CampfireSDK
}

// NewNamingHierarchy creates a NamingHierarchy backed by the given SDK.
func NewNamingHierarchy(sdk CampfireSDK) *NamingHierarchy {
	return &NamingHierarchy{sdk: sdk}
}

// Init creates the root and API campfires (or resumes from env vars) and
// publishes all embedded convention declarations to freesims.api.
func (n *NamingHierarchy) Init(ctx context.Context) error {
	n.mu.Lock()
	defer n.mu.Unlock()

	// Env overrides for resumption across restarts
	if id := os.Getenv("FREESIMS_CF_ROOT"); id != "" {
		n.RootID = id
	}
	if id := os.Getenv("FREESIMS_CF_API"); id != "" {
		n.APIID = id
	}

	if n.RootID == "" {
		if n.sdk == nil {
			return fmt.Errorf("create freesims root campfire: no CampfireSDK provided")
		}
		id, err := n.sdk.Create(ctx, "freesims")
		if err != nil {
			return fmt.Errorf("create root campfire: %w", err)
		}
		n.RootID = id
		log.Printf("[naming] created freesims root: %s", id)
	}

	if n.APIID == "" {
		if n.sdk == nil {
			return fmt.Errorf("create freesims.api campfire: no CampfireSDK provided")
		}
		id, err := n.sdk.Create(ctx, "freesims.api")
		if err != nil {
			return fmt.Errorf("create api campfire: %w", err)
		}
		n.APIID = id
		log.Printf("[naming] created freesims.api: %s", id)
	}

	return n.publishDeclarations(ctx)
}

func (n *NamingHierarchy) publishDeclarations(ctx context.Context) error {
	entries, err := conventionFiles.ReadDir("conventions")
	if err != nil {
		return fmt.Errorf("read embedded conventions: %w", err)
	}

	published := 0
	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".json" {
			continue
		}
		data, err := conventionFiles.ReadFile("conventions/" + entry.Name())
		if err != nil {
			return fmt.Errorf("read convention %s: %w", entry.Name(), err)
		}
		op := strings.TrimSuffix(entry.Name(), ".json")
		opTag := "freesims:" + op

		_, err = n.sdk.Send(ctx, n.APIID, data, []string{
			"convention:operation",
			opTag,
		})
		if err != nil {
			return fmt.Errorf("publish declaration %s: %w", entry.Name(), err)
		}
		published++
	}

	log.Printf("[naming] published %d convention declarations to freesims.api (%s)", published, n.APIID)
	return nil
}
