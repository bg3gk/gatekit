"""Starts the game in one level after another and records what each gives a test: does it
load from a cold start, how long it takes, who the player is, how much is in it, and which
component types are alive there. Writes research/coverage/levels.json (a catalogue for
GK.Stage users) and prints a table.

    python research/coverage/levels_survey.py                 every candidate
    python research/coverage/levels_survey.py Basic_Level_A WLD_Main_A

Uses a stand instance of its own ("survey", bridge-only Mods folder), one game at a time;
the coverage instance is not touched. A level that does not reach "Running" within the
timeout is recorded as failed and the game is killed.
"""
import json
import os
import subprocess
import sys
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
CATALOGUE = HERE / "levels.json"
MODULE = "GustavX"
TIMEOUT = 420  # seconds for one `up`

# name: what it is (from the game data index: placed characters / items)
CANDIDATES = {
    "TUT_Avernus_C": "the Nautiloid tutorial (56 characters, 246 items)",
    "Basic_Level_A": "the engine's basic level, nothing placed",
    "TestLevel_Tom": "a developer's test level (22 items)",
    "WLD_Main_A": "Act 1 (766 characters)",
    "CRE_Main_A": "the githyanki creche (117 characters)",
    "SCL_Main_A": "Act 2 (656 characters)",
    "INT_Main_A": "the interlude between Acts 2 and 3 (36 characters)",
    "BGO_Main_A": "Act 3, Rivington (529 characters)",
    "CTY_Main_A": "Act 3, the Lower City (1457 characters)",
    "IRN_Main_A": "the Iron Throne (47 characters)",
    "END_Main": "the finale (312 characters)",
    "WLD_Campfire_E": "an Act 1 camp",
    "CMP_CentralCampfire_Day": "a camp level",
    "SYS_CC_I": "character creation",
    "END_NetherBrainArenas_E": "an arena of the finale",
}

FACTS = """
local host = Osi.GetHostCharacter()
local types, holders = 0, 0
if Ext.Debug.GetComponentCounts then
    for _, count in pairs(Ext.Debug.GetComponentCounts()) do types = types + 1; holders = holders + count end
end
local party = {}
for _, entity in ipairs(Ext.Entity.GetAllEntitiesWithComponent("PartyMember")) do
    party[#party + 1] = entity.ServerCharacter and entity.ServerCharacter.Template and entity.ServerCharacter.Template.Name or "?"
end
local x, y, z = Osi.GetPosition(host)
return {
    host = host, hostTemplate = Osi.GetTemplate(host), party = party,
    level = Ext.Entity.Get(host).Level and Ext.Entity.Get(host).Level.LevelName,
    region = Osi.GetRegion(host), position = { x, y, z },
    characters = #Ext.Entity.GetAllEntitiesWithComponent("ServerCharacter"),
    items = #Ext.Entity.GetAllEntitiesWithComponent("ServerItem"),
    entities = #Ext.Entity.GetAllEntities(), componentTypes = types,
    hostDead = Osi.IsDead(host) == 1, gameState = tostring(Ext.Utils.GetGameState()),
}
"""


def stand(*arguments: str, timeout: float = 120) -> subprocess.CompletedProcess:
    environment = dict(os.environ, BG3_GATEKIT_INSTANCE="survey", BG3_GATEKIT_START_TIMEOUT="240")
    return subprocess.run([sys.executable, str(HERE / "clean_stand.py"), *arguments], capture_output=True,
                          text=True, encoding="utf-8", errors="replace", timeout=timeout, env=environment)


def survey(level: str) -> dict:
    entry = {"level": level, "what": CANDIDATES.get(level, ""), "module": MODULE,
             "surveyed": time.strftime("%Y-%m-%d")}
    started = time.time()
    try:
        up = stand("up", "--level", f"{MODULE}:{level}", timeout=TIMEOUT)
        entry["seconds"] = round(time.time() - started)
        entry["loads"] = up.returncode == 0 and "playable after" in up.stdout
        if not entry["loads"]:
            entry["problem"] = (up.stdout + up.stderr).strip().splitlines()[-1][:300] if (up.stdout + up.stderr).strip() else "no output"
        else:
            facts_file = HERE / "work" / "survey-facts.lua"
            facts_file.parent.mkdir(exist_ok=True)
            facts_file.write_text(FACTS, encoding="utf-8")
            time.sleep(5)  # let the level settle
            facts = stand("exec", "server", f"@{facts_file}")
            try:
                entry.update(json.loads(facts.stdout))
            except ValueError:
                entry["problem"] = (facts.stdout + facts.stderr).strip()[-300:]
    except subprocess.TimeoutExpired:
        entry["seconds"], entry["loads"], entry["problem"] = round(time.time() - started), False, "timed out"
    finally:
        try:
            stand("down", timeout=120)
        except subprocess.TimeoutExpired:
            pass
    return entry


def main() -> int:
    wanted = sys.argv[1:] or list(CANDIDATES)
    catalogue = json.loads(CATALOGUE.read_text(encoding="utf-8")) if CATALOGUE.exists() else {}
    for level in wanted:
        entry = survey(level)
        catalogue[level] = entry
        CATALOGUE.write_text(json.dumps(catalogue, indent=1, ensure_ascii=False), encoding="utf-8")
        print(f"{level:28} {'loads' if entry.get('loads') else 'FAILS':6} {entry.get('seconds', 0):4} s  "
              f"characters {entry.get('characters', '-')}, items {entry.get('items', '-')}, "
              f"component types {entry.get('componentTypes', '-')}, party {entry.get('party', '-')}"
              f"{'  ' + entry['problem'] if entry.get('problem') else ''}", flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
