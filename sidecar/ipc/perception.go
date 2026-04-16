/*
 * This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
 * If a copy of the MPL was not distributed with this file, You can obtain one at
 * http://mozilla.org/MPL/2.0/.
 */

package ipc

// Clock holds the in-game time emitted by PerceptionEmitter (§reeims-d43).
// day_of_week is absent — VMClock does not track it.
type Clock struct {
	Hours     int `json:"hours"`
	Minutes   int `json:"minutes"`
	Seconds   int `json:"seconds"`
	TimeOfDay int `json:"time_of_day"`
	Day       int `json:"day"` // DayOfMonth from VMClock
}

// Perception is the JSON structure sent by the game when a controlled Sim idles.
type Perception struct {
	Type             string         `json:"type"`              // always "perception"
	PersistID        uint32         `json:"persist_id"`
	SimID            int            `json:"sim_id"`
	Name             string         `json:"name"`
	Funds            int32          `json:"funds"`             // controlled Sim's current budget (§reeims-2ca)
	Clock            Clock          `json:"clock"`             // in-game time (§reeims-d43)
	Motives          Motives        `json:"motives"`
	Position         Position       `json:"position"`
	Rotation         float32        `json:"rotation"`
	CurrentAnimation string         `json:"current_animation"`
	ActionQueue      []QueuedAction `json:"action_queue"`
	NearbyObjects    []NearbyObject `json:"nearby_objects"`
}

// Motives holds the eight core motive values plus derived mood.
type Motives struct {
	Hunger  int16 `json:"hunger"`
	Comfort int16 `json:"comfort"`
	Energy  int16 `json:"energy"`
	Hygiene int16 `json:"hygiene"`
	Bladder int16 `json:"bladder"`
	Room    int16 `json:"room"`
	Social  int16 `json:"social"`
	Fun     int16 `json:"fun"`
	Mood    int16 `json:"mood"`
}

// Position is a tile coordinate in the lot.
type Position struct {
	X     int `json:"x"`
	Y     int `json:"y"`
	Level int `json:"level"`
}

// QueuedAction is an entry in the Sim's interaction queue.
type QueuedAction struct {
	Name     string `json:"name"`
	Target   string `json:"target"`
	Priority int    `json:"priority"`
}

// NearbyObject describes an object within perception range.
type NearbyObject struct {
	ObjectID     int           `json:"object_id"`
	Name         string        `json:"name"`
	Position     Position      `json:"position"`
	Distance     int           `json:"distance"`
	Interactions []Interaction `json:"interactions"`
}

// Interaction is a pie-menu entry on a nearby object.
type Interaction struct {
	ID   int    `json:"id"`
	Name string `json:"name"`
}
