# The camera's focus, and what the game draws far from the party

How the game moves its own camera to a thing, how the toolkit asks for the same
(`GK.Camera.Focus`, `Return`, `OnScreen`: `docs/contracts/lua-api.md`, section 22), and what
is and is not drawn where the camera arrives. Watched in the test level, sampled every client tick.

## The game's own focus

On a ranged attack the client's camera entity gets two requests, one to frame the caster and the
target and one to restore, and its combat target is rewritten; the camera glides to the point
between the two, zooms out to fit both (the "rise"), holds while the projectile flies, and glides
back - each way about 0.6 s. On an enemy's turn the same mechanism holds the camera on the
actor. The engine acts only on a combat target that is marked changed. Beyond the camera's snap
distance (50 m by the game's switches) the camera cuts in place of gliding.

The toolkit's focus uses the camera behaviour's own targets and destination, which works in a
fight and outside one; on the player's own turn a write to the combat target is copied and not
obeyed. During a dialogue the game holds the camera and the focus says so. The glide is in real
time on the client's frames: the game's speed does not change it. Settling took 22-35 server
ticks up to 10 m and 51 at 50 m with the snap distance raised for the move.

## A far creature is not drawn

A creature more than about 30 m from every party member is not drawn, though the camera arrives
exactly where it stands. Staged at 10, 25, 35, 45 and 55 m from the only party member:

- at 10 and 25 m the client holds the creature whole (about 120 components); at 35 m and beyond
  it holds a stub of 17 components with no level and a position that is no longer kept up;
- on the server the far creature is the same entity with the same components but for four: it
  has no `Active`, no `AnubisActive`, no animation blueprint and no animation update - the game
  has put it to sleep;
- a chest at 55 m is a stub on the client too (20 components) and is drawn all the same: the
  rule is about creatures;
- giving the sleeping creature the `Active` component by hand did not bring it to the client;
- making another creature that stood 10 m from a sleeping one a follower of the party did: the
  follower and its neighbour came whole to the client at once. So the anchor is the party's own
  members, each with a radius of about 30 m; the camera is no anchor.

So a focus on a far creature shows its place and not the creature. What decides the radius and
how a creature is woken without a party member beside it is in the next section; the focus now
says `drawn = false` with a warning for such a creature, and its tests assert that.

## Who wakes the space, and how the game crosses a long way

The questions the section above left open are answered. Names are in the registry
(`esv::ActivationManager::*`, `esv::ActivationSystemHelper::SetActive`,
`ecl::CameraSystem::SnapFade`).

**Two rules, not one.** The server and the client each decide by their own distance:

| Who decides | Whole under | Gone beyond | What it means |
|---|---|---|---|
| the server's activation manager, characters | 40 m | 45 m | the creature lives: has `Active`, animates, thinks |
| the same, items | 40 m | 45 m | |
| the same, low items | 10 m | 15 m | |
| what the client is sent | about 32 m | 39-41 m | the creature is drawn |

So between 32 and 45 m a creature is awake and not drawn. The "about 30 m" measured earlier was
the client's rule. Measured with teleported creatures: awake at 39 m and not at 40.5; asleep at
45.4 m and not at 44.8.

**The anchors.** Once a tick the manager judges every character of the level and then looks
around each anchor. Anchors are the characters of the party's view, characters asked to be kept
up (`Osi.SetForceUpdate`), characters flagged to spread activation (items only, 10 m), and the
points of running synced timelines. Only an anchor that is a party member wakes other
characters; the others wake items and themselves. The camera is no anchor at any distance.

**One creature, or a whole place.** `Osi.SetForceUpdate(creature, 1)` wakes that creature alone,
and the client receives it whole and draws it. To wake a place, a character standing there has
to be in the party's view: given the party member's component with the host's party and user,
and appended to both lists of the party's view. No portrait appears; every character within
40 m is awake on the next tick and drawn about half a second later; taking it out again puts
them to sleep on the next tick. A follower (`Osi.AddPartyFollower`) is not this: it gets a
portrait, walks to the leader and stayed asleep 120 m away. What such a helper does to the
story is not measured; only a spawned helper was used, never an origin.

**The long way.** When the camera's target is farther than its snap distance (50 m) from where
the camera is, the game does not glide: it fades to black in one tick, cuts within ten ticks,
holds the black and fades in over five ticks. Measured: 45 m glides; 52, 60, 80 and 100 m fade.
The black holds 1.2 s on the test level, 1.4 s in act 1 at a place already awake, 2.3 s at a
place 233 m away that was woken in the same tick - so the fade waits for the place. What exactly
it waits for, and what ends it, is not named. A switch to a far party member and a focus on a far
whole creature go the same road; a focus on a point or on a stub cuts with no fade.

**The leash.** The camera will not go farther from the controlled character than its switches
allow (50 m each of four); a focus at 72 m settled short until all four were raised.

Not found: the server function holding the client's 32 and 40 m; whether timelines and the photo
mode wake anything when tried live.
