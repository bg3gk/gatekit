# A lead: bones of a figure, read and overridden natively

Not built. This file holds what is known, so the work can start from it and not from nothing.
The owner set it aside on 16 November 2026; nothing here is a decision.

## What it is for

A creature posed or partly moved by our own numbers, with no animation resource authored
outside the game: an arm raised over an idle, a held pose. Today this works from Lua only, slowly
and only in developer mode. A native override in the Script Extender fork would make it cheap,
would need no developer mode, and would carry sockets and cloth with the bones.

## What is known (each with what it stands on)

- **Where the pose lives.** `entity.Visual.Visual.SkeletonSlots[i].Skeleton` is a
  `gr::GrannySkeleton` (type in the registry). Slot 1 is the body (120 bones on human and elf
  rigs), another slot the head. Its fields, read live: the skeleton's bones with names and
  parents; a live world pose; a second, deferred world pose; a live local pose; a rest world and
  a rest local pose; the mesh bindings. Each mesh binding (`gr::MeshBinding` in the registry - the
  name is ours, to be checked) holds the skinning matrices and the skeleton bone of every mesh
  bone. Proof: a named wrist rose from 0.97 to 2.04 m and came back while a wave played
  (`test:figure/client_bones`).
- **Bones are a client matter.** The server does not carry the pose.
- **What a write does.** A local pose written on a tick is overwritten before anything reads it.
  A world pose written after most systems is lost or not shown. Skinning matrices written after
  the client's `Effect` system are shown, and the game computes them anew every frame.
- **What holds a pose.** Removing the tag `ls::AnimationUpdateComponent` from an entity stops
  its animation on the client; skinning matrices written once then stay. The figure no longer
  breathes. Putting the tag back resumed a figure frozen for ten seconds; one frozen for minutes
  at the end of a death animation did not move again.
- **A bone over a playing animation.** Rewriting, after `Effect` each frame, the skinning
  matrices of the meshes that hold a posed bone works: one arm raised over an idle follows the
  living shoulder, and the body keeps its idle. Cost from Lua: about 9 ms a frame for one arm,
  about 110 ms for a whole body. Not checked frame by frame for flicker; things on sockets and
  cloth were not looked at.
- **The game's own blending** exists inside the animation blueprint: about 0.17 s between two
  animations, about 0.1 s on a stop. The float and the flag of the gameplay animation event are
  not the blend time; what they mean is unknown. The blend does not reach a pose we write, so
  entering and leaving a written pose is ours to smooth.
- **The game's own partial overrides** - a head that turns to a speaker, hands brought to a
  weapon - are known by name only (`ecl::lookat::TargetSystem`, `eoc::SteeringComponent`, the
  blueprint's IK nodes, a skeleton's IK rig resource). They take a target, not a rotation. None
  was driven.

## Where to dig

1. **Read access as plain calls:** the bones of an entity (names, parents) and a bone's live
   world and local transform. The macOS symbols name `gr::GrannySkeleton::GetBoneWorldTransform`
   and `GetBoneLocalTransform`; their Windows twins are not located.
2. **An override applied where the game builds the pose:** inside or just before
   `gr::GrannySkeleton::UpdateMeshBindings` (named by the macOS symbols), through
   `SetBoneLocalTransform` and `BuildWorldPose`, so that the world pose, the sockets and the
   cloth follow. A per-entity table of bone to rotation with a weight; the weight moved over
   time is the smoothing.
3. **Whether the game's own layers can carry our values:** read the blueprint's nodes for an
   additive or partial-body layer and for the look-at; if one takes a rotation or a target we can
   set, it is the better way and brings the game's blending with it.
4. **The two unknown arguments** of the gameplay animation event.

By the rule of the fork - it opens what the game does and adds no logic of its own - the first
and the third are coverage; the second is an addition and should be named as one.

## What exists to start from

- The scene `stand/scenes/withers.lua` on the branch `video-stand`: a pose as a small table of
  bone to angles, entered over a short time and held.
- The probes and notes of the study: the session's notes folder, `night/B1/`.
- A fault found on the way, not a physics matter: an animation named as a death pose is a bank of
  fourteen different poses, one a frame; played as a loop it throws the body about. Whether this
  is what shakes corpses on uneven ground in the game proper was not established.
