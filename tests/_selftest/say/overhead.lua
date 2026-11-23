-- GK.Say.Line writes a text over a creature's head (docs/contracts/lua-api.md, "The camera's focus and a line over a head"): the
-- game shows a status's name over the head of the creature it is applied to, so the line is a
-- status of the toolkit's own (GK_SAY_<slot>, a copy of BLESS with its effects stripped) whose
-- display name, a translated string of the toolkit's own, is the text in the game's markup. The
-- rule held here: the status is on the creature when the verb answers and gone a moment later;
-- two creatures at once keep each its own line (two slots, two strings: rewriting one does not
-- change the other), and a creature's second line while its first still floats takes a third
-- (the same status applied again within those ~3 s shows nothing: measured); the caller's < > & survive as text (escaped to &lt; &gt; &amp;, which the
-- game shows as written); a row `said_overhead` says who and what. What a person sees (no
-- assertion can read it: the screenshot `lines.jpg` in the test's folder, taken after the third
-- line): over the first human "• Look <here> & now •" in bold violet with paler dots and "and again"
-- (the human tinted violet for a moment: style `magic`, the effect `orb`); over the second
-- "~ a whisper ~" in italic lilac; each for about three seconds.
-- Staging (cheat): two neutral humans beside the host; the lines (GK.Say is a cheat: a text the
-- game did not say). The camera's focus on them is fair. Reading: observation.
-- Run: gatekit --instance <copy> test tests -k _selftest/say/overhead --speed working

test { level = "GustavX:Basic_Level_A",
       about = "A line over a head is a status of the toolkit's own named with the text: on while it shows, gone after, one per creature, markup characters kept." }

local function uuid(id) return id and string.match(id, "(%x+%-%x+%-%x+%-%x+%-%x+)$") or id end

local TEXT1, TEXT2 = "Look <here> & now", "a whisper"

function run(game)
    game.wait_state("Running", "server", 120)
    local pair = step("two humans beside the host", "cheat", function()
        local g = game.server([[
            local x, y, z = Osi.GetPosition(Osi.GetHostCharacter())
            return { GK.Stage.Spawn{ template = "human", at = { x + 2, y, z + 2 }, faction = "neutral" }[1],
                     GK.Stage.Spawn{ template = "human", at = { x - 2, y, z + 2 }, faction = "neutral" }[1] } ]])
        game.wait("client", string.format("Ext.Entity.Get(%q) ~= nil and Ext.Entity.Get(%q) ~= nil", g[1], g[2]), 10)
        return { uuid(g[1]), uuid(g[2]) }
    end)
    step("the camera on both", "fair", function()
        check(game.server(string.format("return GK.Camera.Focus({ %q, %q })", pair[1], pair[2]), 30).ok, "the camera framed both")
    end)
    local mark = game.mark()
    local said = step("a line over each, at once", "cheat", function()
        local a = game.server(string.format("return GK.Say.Line(%q, %q, { style = 'magic' })", pair[1], TEXT1))
        local b = game.server(string.format("return GK.Say.Line(%q, %q, { style = 'whisper' })", pair[2], TEXT2))
        check(a.ok and b.ok, "both statuses came on their creatures", a, b)
        return { a, b }
    end)
    step("each its own line, the markup characters kept", "observation", function()
        local a, b = said[1], said[2]
        check(a.status ~= b.status and a.handle ~= b.handle, "two statuses, two strings", a.status, b.status)
        check(contains(a.markup, "Look &lt;here&gt; &amp; now"), "the caller's < > & escaped", a.markup)
        local texts = game.server(string.format("return { Ext.Loca.GetTranslatedString(%q), Ext.Loca.GetTranslatedString(%q) }",
            a.handle, b.handle))
        eq(texts[1], a.markup, "the first line's string, after the second was written")
        eq(texts[2], b.markup, "the second line's string")
        local shown = game.server(string.format("return { GK.Say.Shown(%q), GK.Say.Shown(%q) }", pair[1], pair[2]))
        eq(shown[1].text, TEXT1, "the first creature's line, as given")
        eq(shown[2].text, TEXT2, "the second creature's line")
        for i, who in ipairs(pair) do
            local r = row(mark, "said_overhead", function(p) return uuid(p.who) == who end, 5)
            eq(r.payload.text, i == 1 and TEXT1 or TEXT2, "the row's text")
        end
    end)
    -- "while its first shows" is a condition of time: a slot is free again 3 s (Say.ShowMs) after its
    -- line. The picture used to be taken before this step; a headless game draws 30 frames for a
    -- picture (~1.8 s), which put this step past the 3 s and the first slot was taken again
    -- (W3 round 7). So the third line comes first and the picture after it.
    local again = step("a second line on the first creature while its first shows", "cheat", function()
        local shown = game.server(string.format("return GK.Say.Shown(%q)", pair[1]))
        check(shown and shown.text == TEXT1, "the first line still shows before the second", shown)
        return game.server(string.format("return GK.Say.Line(%q, 'and again')", pair[1]))
    end)
    step("it took another status: the same one again would show nothing", "observation", function()
        check(again.ok and again.status ~= said[1].status and again.status ~= said[2].status,
            "a third status for the third line", again.status)
    end)
    step("a picture of the lines", "observation", function() game.shot("lines.jpg", 0) end)
    said[3] = again
    pair[3] = pair[1]
    step("the statuses gone after", "observation", function()
        for i, who in ipairs(pair) do
            game.wait("server", string.format("Osi.HasActiveStatus(%q, %q) == 0", who, said[i].status), 10)
        end
    end)
end
