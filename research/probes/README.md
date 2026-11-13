# probes — Lua written to answer one question

Each of these was written to find out one thing inside a running game and then kept, because the
answer is in the code and the next person will ask the same question. They are not tests and not
examples: nothing checks them, and some of them failed, which is the finding.

Run one through the bridge, from the host:

```bash
./gatekit --instance <yours> exec client @research/probes/ui_tree.lua
```

| | |
|---|---|
| `ui_tree.lua` | walks the Noesis visual tree and reports the distinct `DataContext` types with the path of the first element carrying each |
| `ui_context.lua` | the properties of the first `DataContext` of a given type (set the global `WANT` first) |
| `ui_dialog_props.lua` | the dialogue view model and its data context, property by property |
| `client_explore.lua` | what can be read about the UI, the input state and the controlled character |
| `timeline_tracks.lua` | every live timeline with the kinds of track it carries, in full for the tracks that decide whether weapons and bodies are shown |
| `controller_inject.lua` | an attempt to make the game believe a controller button was pressed, by putting a raw input change into the input manager's inject queue. This is the one that did not work; switching the UI between keyboard and controller from code is still on the "not there yet" list |
