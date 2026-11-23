-- A test's files reach the game (docs/writing-tests.md, "Files beside a test"): the runner puts
-- hello.txt, kept beside this test, into the copy as Data/GateKit_selftest/hello.txt before the
-- test, in a folder it makes, and takes both out after it. The game reads the file from its own
-- data folder through Script Extender (Ext.IO.LoadFile with the "data" context), byte for byte.
-- Staging: none (the runner's). Reading: observation.
-- Run: gatekit --instance <copy> test tests -k _selftest/files

test { menu = true, files = { ["Data/GateKit_selftest/hello.txt"] = "hello.txt" },
       about = "A file the header's files names is in the copy's game data when the test runs." }

function run(game)
    step("the game reads the file", "observation", function()
        local text = game.client([[return Ext.IO.LoadFile("GateKit_selftest/hello.txt", "data")]])
        eq(text, "A file a test keeps beside it, read by the game.\n", "the file as the game reads it")
    end)
end
