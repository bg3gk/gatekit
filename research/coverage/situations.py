"""Situations: short pieces of play that make the game create components which do not exist
in an idle level. check.py runs them while the tick capture is on (lua/probe.lua,
Probe.CaptureStart), so that one-frame and short-lived components are read in the tick they
exist; census.py uses them to list which types each situation brings to life.

A situation is a function taking the stand's `game`; it must leave the game as it found it
(nobody in a dialogue, weapons sheathed), so that situations can run one after another.
They use only the bridge's own calls (docs/api.md): no window focus, no real input.

    SITUATIONS["weapons"](game)
    run(game, ["weapons", "dialogue"])
"""
import time
from pathlib import Path

# The brine pool on the Nautiloid: a dialogue with answers, dummies and a camera, present in
# the stand's test level (the same one stand/scenarios/dialogue.py walks).
POOL = "S_TUT_Start_Brinepool_5efb6f66-b2ce-403e-b7c0-b68f607ef13a"


def lua_string(text: str) -> str:
    return "[==[" + text + "]==]"


def leave_dialogue(game) -> int:
    """Chooses the last answer again and again until no dialogue is on screen."""
    steps = 0
    while True:
        # a dice roll or a message box in front of the dialogue: dealt with first, phase by phase
        for _ in range(12):
            if not game.ui("return GK.Unblock and GK.Unblock()"):
                break
            time.sleep(0.7)
        now = game.dialog()
        if not now:
            return steps
        steps += 1
        if steps > 20:
            raise AssertionError(f"the dialogue does not end: {now}")
        if now["answers"]:
            game.ui(f"return GK.Answer({now['answers'][-1]['n']})")
        game.wait("ui", f"(function() local d = GK.Dialog(); return d == nil or d.text ~= {lua_string(now['text'])} "
                        f"or #d.answers ~= {len(now['answers'])} or (GK.Blocking and GK.Blocking().roll ~= nil) end)()",
                  timeout=30)


def weapons(game) -> None:
    """Draws and sheathes the weapons of the controlled character."""
    game.server("return GK.Unsheath(true)")
    time.sleep(3.0)
    game.server("return GK.Unsheath(false)")
    time.sleep(3.0)


def dialogue(game) -> None:
    """Starts the brine pool dialogue, waits for its answers, stays a moment, walks out."""
    leave_dialogue(game)
    known = game.server('return GK.Dialogs("Brinepool")')
    if not known:
        raise AssertionError("the brine pool dialogue is not registered in this level")
    game.wait("server", f'GK.StartDialog("{known[0]["dialog"]}", "{POOL}")', timeout=15, every=1.0)
    game.wait("ui", "(GK.Dialog() or {}).showAnswers and GK.Dialog()", timeout=60)
    time.sleep(2.0)
    leave_dialogue(game)
    time.sleep(2.0)


def dialogue_stay(game) -> None:
    """Starts the brine pool dialogue and stays in it (for reading what lives only inside one);
    leave_dialogue() ends it."""
    leave_dialogue(game)
    known = game.server('return GK.Dialogs("Brinepool")')
    if not known:
        raise AssertionError("the brine pool dialogue is not registered in this level")
    game.wait("server", f'GK.StartDialog("{known[0]["dialog"]}", "{POOL}")', timeout=15, every=1.0)
    game.wait("ui", "(GK.Dialog() or {}).showAnswers and GK.Dialog()", timeout=60)
    time.sleep(1.0)


def equipment(game) -> None:
    """Takes the main-hand weapon off and puts it back on, then switches the weapon set twice."""
    worn = game.server('return GK.Character().equipment["Melee Main Weapon"]')
    if worn:
        game.server(f'return GK.Unequip("{worn["uuid"]}")')
        time.sleep(2.0)
        game.server(f'return GK.Equip("{worn["uuid"]}")')
        time.sleep(2.0)
    for _ in range(2):
        game.ui('return GK.Command("SwitchWeaponSetCommand")')
        time.sleep(2.0)


def potion(game) -> None:
    """Gives the controlled character a healing potion and has them drink it."""
    template = game.server('return Ext.Stats.Get("OBJ_Potion_Healing").RootTemplate')
    game.server(f'return GK.Give("{template}")')
    # potions stack: the new one may join a stack the character already carries
    found = game.wait("server", f'(function() local all = GK.Find("{template}"); return #all > 0 and all end)()',
                      timeout=15)
    game.server(f'Osi.Use(Osi.GetHostCharacter(), "{found[0]}", "") return true')
    time.sleep(5.0)


# GK.Stage (stand/bridge/.../Server/Stage.lua) is loaded into the game by hand until the bridge
# includes it; loading it twice is harmless.
STAGE_LUA = Path(__file__).resolve().parents[2] / "stand" / "bridge" / "Mods" / "Gwynerva_GateKit" /     "ScriptExtender" / "Lua" / "Server" / "Stage.lua"


def stage(game) -> None:
    """Makes sure GK.Stage exists in the running game and no tutorial window will interrupt."""
    # a level or a save may still be loading: nothing can be staged before the host has a user
    if (game.state("server") or {}).get("gameState") != "Running" or not game.server(
            "return Osi.GetHostCharacter() ~= nil and Osi.GetReservedUserID(Osi.GetHostCharacter()) ~= nil"):
        game.wait("server", '(function() if tostring(Ext.Utils.GetGameState()) ~= "Running" then return false end '
                            'local id = Osi.GetHostCharacter() return id ~= nil and Osi.GetReservedUserID(id) ~= nil end)()',
                  timeout=400, every=1.0)
    if not game.server("return GK.Stage ~= nil and GK.Stage.Quiet ~= nil"):
        game.server(STAGE_LUA.read_text(encoding="utf-8"))
    # Cutscenes (the level's opening one after a start or a world reset) block fights and
    # dialogues until they end: play them twenty times faster for the whole run.
    game.fast_scenes(20)
    # A dead party is a "game over" window and a paused server: keep the party unkillable for
    # the whole run (GK.Stage.Reset() lifts the protection, so it is renewed here every time).
    # ... and raised at once should a script kill somebody anyway (a scripted death is not
    # stopped by being unkillable). One call for all three: every call costs a round trip.
    game.server("GK.Stage.Quiet() GK.Stage.Protect(true) if GK.Stage.Guard then GK.Stage.Guard(true) end return true")


def fight(game) -> None:
    """Three goblins attack; the host strikes one, turns go round three times, a goblin is
    burned, killed and raised again, the fight is ended and the scene removed."""
    stage(game)
    goblins = game.server('return GK.Stage.Spawn{ template = "goblin", count = 2, faction = "enemy", '
                          'equipment = { "longsword", "shield" } }')
    goblins += game.server('return GK.Stage.Spawn{ template = "goblin_ranger", faction = "enemy", distance = 5 }')
    time.sleep(2.0)
    listed = "{" + ", ".join(f'"{uuid}"' for uuid in goblins) + "}"
    game.server(f"return GK.Stage.Fight({listed})")
    game.wait("server", "(GK.Stage.Combat() or {}).active ~= nil", timeout=20)
    game.server(f'return GK.Stage.Cast("Target_MainHandAttack", "{goblins[0]}")')
    time.sleep(4.0)
    for _ in range(9):  # three rounds of three or four participants
        game.server("return GK.Stage.EndTurn()")
        time.sleep(1.5)
    game.server(f'return GK.Stage.Status("BURNING", "{goblins[1]}", 2)')
    game.server(f'return GK.Stage.Damage(2, "{goblins[1]}", "Fire")')
    time.sleep(1.5)
    game.server(f'return GK.Stage.Kill("{goblins[0]}")')
    time.sleep(2.5)
    game.server(f'return GK.Stage.Resurrect("{goblins[0]}")')
    time.sleep(3.0)
    game.server("return GK.Stage.Reset()")  # removes the goblins and ends the fight
    stage(game)
    game.server("return GK.Stage.Heal()")
    time.sleep(3.0)


def surface(game) -> None:
    """Fire on the ground around a chest some steps away, then turn-based mode on and off."""
    stage(game)
    chest = game.server('return GK.Stage.Spawn{ template = "chest", distance = 9 }')
    time.sleep(1.5)
    game.server(f'return GK.Stage.Surface("SurfaceFire", 2, GK.Stage.Position("{chest[0]}"))')
    time.sleep(3.0)
    game.server("return GK.Stage.TurnBased(true)")
    time.sleep(3.0)
    game.server("return GK.Stage.TurnBased(false)")
    time.sleep(2.0)
    game.server("return GK.Stage.Reset()")
    stage(game)
    time.sleep(2.0)


SITUATIONS = {"weapons": weapons, "dialogue": dialogue, "equipment": equipment, "potion": potion,
              "fight": fight, "surface": surface}


def run(game, names: list[str]) -> None:
    for name in names:
        SITUATIONS[name](game)
