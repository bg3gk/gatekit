-- Smoke test: the game starts, both sides of the bridge answer, the host character can be read.

scenario { level = "GustavX:TUT_Avernus_C", mods = {} }

function run(game)
    local version = game.client("return Ext.Utils.Version()")
    check(version and version >= 33, "Script Extender version", version)

    game.wait_state("Running", "server", 60)
    local host = game.server("return Osi.GetHostCharacter()")
    check(host, "no host character")

    local dump = game.server(([[
        local e = Ext.Entity.Get("%s")
        return GK.Dump(e, { "DisplayName", "Level", "PartyMember" })
    ]]):format(host))
    print("host: " .. host .. "  components: " .. join(keys(dump)))
    game.shot("smoke.jpg")
end
