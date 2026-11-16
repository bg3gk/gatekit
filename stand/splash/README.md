# The stand's own splash

The channel's logo, centred on black, over the game's "press any key" screen: it fades in, stays
a few seconds and fades out, and the statue with "press any key" is on the screen again. Escape,
Enter or the space bar take it off at once; the logo takes those keys, so the screen under it
does not hear them and stays.

    ./gatekit --instance <copy> up --splash
    ./gatekit --instance <copy> exec ui "$(cat stand/splash/logo.lua)"       # show
    ./gatekit --instance <copy> exec ui "$(cat stand/splash/logo_off.lua)"   # take off now

UI context. The picture is `GK.Show.Picture` (docs/contracts/lua-api.md, "GK.Show"); the scene
adds the options and a listener on the row `picture_hidden`. State: `GKScene.splash_logo =
{ armed, fired, handle, under, left = { why, key, shownMs }, after, log }` (`under` and `after`:
what `GK.Gui.Stuck()` named before the logo and after it: `splash` both times).

## The logo's file

The logo is the owner's own picture and is not in the repository. The game reads a picture
through its own file system only, so the file has to lie under the copy's `Data` folder; the
scene's default is `Stand/channel-logo-chip.png` there. Once per copy:

    mkdir "<copy's game folder>/Data/Stand"
    cp <the logo>.png "<copy's game folder>/Data/Stand/channel-logo-chip.png"

A png with transparency is drawn with it.

## Options

`GKSceneOptions = { splash_logo = { logo, height, seconds, fadeIn, fadeOut, skip } }` before the
scene: `logo` a path under Data, `height` the logo's height in parts of the screen (0.56),
`seconds` at full strength (3), the fades in seconds (1.2 and 0.9), `skip` the keys
(`{ "escape", "enter", "space" }`).

## Limits

- **Not before the splash screen.** The scene is run through the bridge, and `up --splash`
  answers when the splash screen stands; so the statue is seen for a moment before the logo
  covers it. The client's Lua runs earlier (from the state `LoadModule`), but nothing of the
  toolkit runs a scene by itself at the start.
- **Not in the game's own screenshot.** The logo is drawn by Script Extender's drawing layer,
  after the frame `gatekit shot` copies; a recording of the screen shows it.
- **The studio's own logo video** is not played in a copy: `gatekit up` puts an empty file in its
  place (docs/contracts/cli.md, 2.3.6).
