# agent-play — an agent playing the game, honestly

The goal: an agent plays Baldur's Gate 3 the way a player does — reading the situation as the
party perceives it, acting only through what a player could do — with no screenshots and no
knowledge the party has not earned. The prologue first, the whole game later.

| | |
|---|---|
| [`agent-play.md`](agent-play.md) | the journal of the attempt: what was built, what the runs showed, where it stands |
| `agent-play-blind-notes.md`, `agent-play-blind3-notes.md` | what the agent wrote down while playing, run by run |
| `agent-play-blind-wishes.md`, `agent-play-blind3-wishes.md` | the wishes those runs produced: every place where the API lied, hid something, or made the agent guess. This is the valuable part — each item is a defect in the honest-play API, written by the thing that hit it |
| [`agent-immersive.md`](agent-immersive.md) | an optional addition to a brief, for a run meant to be watched or read as a story rather than only measured |
| [`briefs/`](briefs) | what an agent is given before it plays: the wrapper, the tasks (`explore`, `prologue`), the addons (`tester`, `newcomer`) and worked examples. A brief is made of independent parts, and none of them names a place the agent has not been |

The notes and wishes quote the game in the language it was running in, so a few lines are in
Russian: they are the game's own text, kept verbatim.

The API these runs drove — `GK.See`, `GK.Act`, `GK.Heard`, `GK.Camera`, `GK.Focus` — is part of
the product; see `docs/contracts/lua-api.md`. The runner and the briefs here were Python around
the old stand and have not been ported.
