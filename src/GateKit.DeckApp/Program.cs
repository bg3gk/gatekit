using System.CommandLine;
using GateKit.Deck;

// `gatekit-deck`: opening the deck without a console window existing at any point.
//
// `gatekit deck` does the same thing and is the one to type in a terminal. But `gatekit` is a
// console program, and Windows gives a console program a console window whenever it is started
// without one - from a shortcut, from Explorer, from another program. That window is not the
// deck, nobody reads it, and worst of all a new console takes the keyboard the moment it appears,
// which is the exact thing the deck exists to stop. It can be hidden from inside, and that leaves
// a flash of about forty milliseconds: the window exists before the program's first instruction,
// so no amount of care inside the program removes it.
//
// A program built for the window subsystem never gets one. That is the whole of this project: no
// logic of its own, the same command, no console at any point. Anything that starts the deck
// without a terminal - a shortcut, a script, a tool - should start this.
//
// Its output goes where the deck's output goes: the log beside the deck's state, which it would
// have used anyway, having no console to write to.
var deck = DeckCommands.CreateCommand();
return await deck.Parse(args, new ParserConfiguration { ResponseFileTokenReplacer = null }).InvokeAsync();
