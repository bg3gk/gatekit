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
whether a creature can be woken without a party member beside it is not found. The focus's
test of a far creature passes on the camera's place alone and does not say that the creature is
not drawn: that is owed to the test and to the verb's answer.
