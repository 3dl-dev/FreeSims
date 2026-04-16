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
	LotAvatars       []LotAvatar    `json:"lot_avatars"`
	Skills           Skills         `json:"skills"`             // TS1 skill values (§reeims-edc)
	Job              Job            `json:"job"`                // career state (§reeims-930)
	Relationships    []Relationship `json:"relationships"`      // per-Sim relationship scores (§reeims-2eb)
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

// Skills holds the six TS1 skill values for the controlled Sim (§reeims-edc).
// PersonData indices: Cooking=10, Charisma=11, Mechanical=12, Creativity=15, Body=17, Logic=18.
// Values are in the range [0, 1000].
type Skills struct {
	Cooking    int16 `json:"cooking"`
	Charisma   int16 `json:"charisma"`
	Mechanical int16 `json:"mechanical"`
	Creativity int16 `json:"creativity"`
	Body       int16 `json:"body"`
	Logic      int16 `json:"logic"`
}

// Job holds the Sim's current career state (§reeims-930).
//
// has_job is true when PersonData[JobType] > 0 (CARR chunk ID is the career track).
// career is the career track name from the CARR chunk; null in this fork because
// TS1JobProvider (Content.Jobs) is not wired — CARR name lookup is a known gap.
// level is the 0-based promotion level within the track (PersonData[JobPromotionLevel]).
// salary and work_hours are zero/null for the same reason — require TS1JobProvider.
type Job struct {
	HasJob    bool    `json:"has_job"`
	Career    *string `json:"career"`     // null: TS1JobProvider not wired (known gap)
	Level     int     `json:"level"`
	Salary    int     `json:"salary"`     // 0: TS1JobProvider not wired (known gap)
	WorkHours *struct {
		Start int `json:"start"`
		End   int `json:"end"`
	} `json:"work_hours"` // null: TS1JobProvider not wired (known gap)
}

// LotAvatarMotives holds the eight core motive values plus derived mood for a lot avatar.
// Note: room motive is excluded (not part of the lot_avatars spec — reeims-d37).
type LotAvatarMotives struct {
	Hunger  int16 `json:"hunger"`
	Comfort int16 `json:"comfort"`
	Energy  int16 `json:"energy"`
	Hygiene int16 `json:"hygiene"`
	Bladder int16 `json:"bladder"`
	Social  int16 `json:"social"`
	Fun     int16 `json:"fun"`
	Mood    int16 `json:"mood"`
}

// LotAvatar describes another Sim present on the lot (reeims-d37).
// Self (the Sim whose perception is being emitted) is excluded by persist_id.
//
// godMode shape (FREESIMS_GOD_MODE=1): Motives is populated; LooksLike is empty.
// Embodied shape (default): LooksLike is a ≤60-char synthesized description of the
// other Sim's observable state; Motives is zero-valued (reeims-5e3).
type LotAvatar struct {
	PersistID        uint32           `json:"persist_id"`
	Name             string           `json:"name"`
	Position         Position         `json:"position"`
	CurrentAnimation string           `json:"current_animation"`
	Motives          LotAvatarMotives `json:"motives"`
	LooksLike        string           `json:"looks_like,omitempty"`
}

// Relationship describes the controlled Sim's relationship to another avatar (reeims-2eb).
//
// other_persist_id and other_name identify the counterpart.
// friendship is RelVar[0] from VMEntity.MeToObject; range [-1000, 1000]; 0 when
// no interaction has ever occurred.
// family_tag is null in VMLocalDriver mode — TS1 family membership is stored in the
// neighbourhood.iff NGBH chunk which is not loaded at runtime for in-lot perception.
// is_roommate is true when the other avatar's VMTSOAvatarState.Permissions >= Roommate.
type Relationship struct {
	OtherPersistID uint32  `json:"other_persist_id"`
	OtherName      string  `json:"other_name"`
	Friendship     int     `json:"friendship"`
	FamilyTag      *string `json:"family_tag"`  // null: not tracked at runtime (known gap)
	IsRoommate     bool    `json:"is_roommate"`
}

// PathfindFailed is the JSON structure sent by the game when a pathfind fails
// with a terminal HardFail (reeims-9e7). Only top-level routing frames emit this
// event — nested portal sub-routes do not.
//
// reason values: "no-route", "no-path", "no-room-route", "blocked",
// "cant-sit", "cant-stand", "no-valid-goals", "locked-door",
// "interrupted", "unknown".
type PathfindFailed struct {
	Type           string `json:"type"`             // always "pathfind-failed"
	SimPersistID   uint32 `json:"sim_persist_id"`
	TargetObjectID int    `json:"target_object_id"` // 0 when no specific target
	Reason         string `json:"reason"`
}

// DialogEvent is the JSON structure sent by the game when a Sim receives a
// dialog box (reeims-9be). The agent should respond with a dialog-response
// command matching the dialog_id.
//
// buttons contains only the non-null button labels in order: Yes, No, Cancel.
// An empty buttons array means the dialog is a notification (no choice required).
//
// sim_persist_id identifies the Sim whose thread is blocked waiting for the
// dialog response.
type DialogEvent struct {
	Type         string   `json:"type"`           // always "dialog"
	DialogID     int      `json:"dialog_id"`
	SimPersistID uint32   `json:"sim_persist_id"`
	Title        string   `json:"title"`
	Text         string   `json:"text"`
	Buttons      []string `json:"buttons"`
}
