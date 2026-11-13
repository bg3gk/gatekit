# Offering changes to Norbyte

GateKit's fork of the Script Extender exists because we needed more of the engine visible from
Lua than it showed. Most of what is in it is not ours to keep: it is a set of improvements to
[Norbyte's work](https://github.com/Norbyte/bg3se), and it belongs back in his repository if he
wants it.

This page is how that is done, so it is done the same way every time. The one change prepared so
far is [`sound-settings.md`](sound-settings.md): its description, its commit message, and where
the branch is. Nothing has been pushed.

## The order

1. **One small change first.** The sound settings mapping, prepared in this folder. Small,
   self-contained, obviously correct, and useful to anyone — not only to us.
2. **A conversation, not a delivery.** Once there is contact, ask how he would like the rest: in
   what size, in what order, with what evidence. A maintainer knows the shape his own project
   should keep.
3. **Then the rest, in his shape.** Forty commits arriving unannounced is not a contribution, it
   is a demand that someone else read forty commits. Agreeing the form first costs one message
   and saves everyone a month.

## Asking, in the first one

The first pull request ends with a short question — after the change, never instead of
explaining it. Two or three sentences, roughly:

> There is a fair amount more of this kind of work: component layouts and field names, each
> checked in a running game. If any of it is useful to you, tell us the shape you would like it
> in — how large a change should be, how comments and names should read, what evidence you want
> with it — and we will follow that. If you would rather not have it, say so and we will keep it
> to our fork; no explanation needed.

Three things that wording is doing, and all three matter:

- **It makes "no" easy.** A maintainer who feels obliged to accept help will quietly stop
  replying instead.
- **It hints at the size without naming it.** "A fair amount more" tells him this is not a
  one-off and that he may want to think about form — which is the point of asking. Naming forty
  commits in the first message is a threat rather than an offer; let him ask how much.
- **It asks about form, not about merit.** Whether a change is good is his judgement; how it
  should be shaped is knowledge only he has, and asking for it costs him one message.

**What the first one does not say.** Some of our work corrects mistakes in his — a field named
for the wrong thing, a structure a few bytes short. Do not raise that in the first pull request,
however gently. It reads as a claim about him rather than about the code, the first message
decides whether a second is read, and a correction with live evidence attached makes the point
by itself when it arrives. Linking to our fork there is the same mistake twice over: it invites
him to read the forty commits we have just promised not to dump on him.

Say it after he answers about form, and say it as a warning rather than a claim: *some of these
change what a field means, not only its layout — we would send those one at a time, each with
what was checked in a running game.* That is useful to him, because a change of meaning breaks
other people's mods and he will want to review those differently.

Whatever he answers becomes this file's rules, replacing anything here that disagrees with him.

## How a change is prepared

- **A branch cut from upstream**, not from `gatekit-v34`, carrying that change and nothing else.
  No GateKit naming, no mention of us in the code.
- **In the style of the code around it.** Match what is already in the file: names, bracing,
  comment voice. The change should be unremarkable to read.
- **Comments strictly to the point.** A comment says what a reader cannot see: where a layout
  came from, which function proved it, what was checked and what was inferred. It never narrates
  what the line already says, never apologises, and never mentions GateKit.
- **A description made of evidence.** What the structure is, how it was established, what it was
  checked against, in a running game or in the binary. Not assertion.
- **The honest unknowns, written down.** "I could not determine what the third float is" costs
  nothing. A confident wrong claim costs the next person's trust in everything else we send.

## Whose name is on it

A pull request has exactly one author on GitHub; two accounts cannot open one together. So:

- **The first one comes from [@Gwynerva](https://github.com/Gwynerva)** — a person arriving as a
  person.
- **The ones after it come from the bot**, once there is a working relationship and nobody is
  guessing who we are.
- **Either way the commits credit both**, with the usual `Co-Authored-By: GateKit Thrall`, which
  is the one place GitHub does allow two names.

That AI agents did much of the work is said plainly in the first pull request, once, without
ceremony — the same honesty the README gives. What must not happen is a maintainer discovering it
later and feeling handled.

## When

**After GateKit is published**, not before. The fork has to be on GitHub for a pull request to
come from it, and arriving with an organisation someone can look at — the toolkit, the fork, the
maps, the documentation — says more about whether this is worth his time than any description in
the pull request could.
