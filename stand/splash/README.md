# The stand's own splash

The channel's logo, centred on black, over the game's "press any key" screen: it fades in, stays
a few seconds and fades out, and the statue with "press any key" is on the screen again. Escape,
Enter or the space bar take it off at once; the logo takes those keys, so the screen under it
does not hear them and stays.

    ./gatekit --instance <copy> up --splash                                   # in the background
    ./gatekit --instance <copy> exec ui "$(cat stand/splash/logo.lua)"       # show, at the bridge's first answer
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

`GKSceneOptions = { splash_logo = { logo, height, seconds, fadeIn, fadeOut, skip, hold, hold_max } }`
before the scene: `logo` a path under Data, `height` the logo's height in parts of the screen
(0.56), `seconds` at full strength (3), the fades in seconds (1.2 and 0.9), `skip` the keys
(`{ "escape", "enter", "space" }`), `hold` the scenes it waits for (names of `GKScene`: it stays
at full strength until each says `ready` or `error`, at most `hold_max` seconds, 40), so that the
figures they make under it stand in their pose when the screen is first seen.

## When it shows: the order of a start

The game's own startup video (`Video/Splash_Logo_Larian.bk2` in `Gustav_Video.pak`, the only
startup video the game ships) plays first, before the client's Lua lives: the Larian Studios logo,
then NVIDIA GameWorks, both in that one video (seen on screen: about 6 s and 8.5 s after the
window opens). The bridge first answers in the state `LoadModule`, right after it; then the game's
loading picture stands during `LoadMenu` (about twenty seconds) and the splash screen comes in
`Menu`. A copy plays the video only when started with `up --startup-videos` (every other start
skips it). The logo is put up at the bridge's first answer: `up --splash --startup-videos` is
begun in the background and `U` is asked until it answers. It is then over black before the
loading picture and the splash screen are seen, and leaves when the scenes it holds for are ready:

    Larian Studios, NVIDIA (the game's video) -> the logo on black (from LoadModule) -> the splash screen with its company

## Limits

- **The startup video is played only when asked**: a start without `--startup-videos` shadows it
  with an empty loose file, so the video for the film needs that option on the `up` that begins
  the run (it costs about eight seconds of the start).
- **Nothing runs the logo by itself**: a driver asks the bridge until it answers. The earliest
  moment is the client's Lua's first answer, after the startup video.
- **Not in the game's own screenshot.** The logo is drawn by Script Extender's drawing layer,
  after the frame `gatekit shot` copies; `gatekit shot --overlay` and a recording of the screen
  show it. Nothing of the bridge answers during the startup video, so `gatekit shot` takes no
  picture of it.
