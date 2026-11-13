-- Weave, part 1: templates. A small XML reader - Script Extender's Lua has none.
--
-- Understands elements, attributes in "double" or 'single' quotes, self-closing tags, text,
-- comments, <?...?> and <!...> declarations (skipped), and the entities &lt; &gt; &amp; &quot;
-- &apos; &#NN; &#xHH;. No namespaces, no CDATA. Errors carry the line number.
--
--   Weave.ParseXml(text) -> node | nil, "line N: what is wrong"
--   node = { tag, attrs = { name = value }, order = { attribute names as written },
--            children = { node, ... }, text = "the element's own text", line }

Weave = Weave or {}

local ENTITIES = { lt = "<", gt = ">", amp = "&", quot = '"', apos = "'" }

local function decode(text)
    if not text:find("&", 1, true) then return text end
    return (text:gsub("&(#?x?)([%w]+);", function(kind, body)
        if kind == "#" then return utf8.char(tonumber(body) or 63) end
        if kind == "#x" then return utf8.char(tonumber(body, 16) or 63) end
        if kind == "" and ENTITIES[body] then return ENTITIES[body] end
        return "&" .. kind .. body .. ";"
    end))
end

-- Text as written in an indented template: the indentation shared by its lines is not part
-- of it, nor are the blank lines around it.
local function dedent(text)
    text = text:gsub("\r", "")
    if not text:find("\n", 1, true) then return (text:match("^%s*(.-)%s*$")) end
    local lines, shortest = {}, nil
    for line in (text .. "\n"):gmatch("(.-)\n") do
        lines[#lines + 1] = line
        if line:find("%S") then
            local indent = #line:match("^[ \t]*")
            if not shortest or indent < shortest then shortest = indent end
        end
    end
    while lines[1] and not lines[1]:find("%S") do table.remove(lines, 1) end
    while lines[#lines] and not lines[#lines]:find("%S") do lines[#lines] = nil end
    for i, line in ipairs(lines) do lines[i] = line:sub((shortest or 0) + 1):gsub("%s+$", "") end
    return table.concat(lines, "\n")
end

function Weave.ParseXml(source)
    local pos, line = 1, 1
    local length = #source

    local function fail(message)
        error({ xml = "line " .. line .. ": " .. message }, 0)
    end

    local function advance(to)
        local _, breaks = source:sub(pos, to - 1):gsub("\n", "")
        line = line + breaks
        pos = to
    end

    local function parseAttributes(node)
        while true do
            local _, afterSpace = source:find("^%s*", pos)
            advance(afterSpace + 1)
            local char = source:sub(pos, pos)
            if char == ">" then advance(pos + 1) return false end
            if source:sub(pos, pos + 1) == "/>" then advance(pos + 2) return true end
            local name = source:match("^[%w_:.@#%-]+", pos)
            if not name then fail("in <" .. node.tag .. ">: expected an attribute, '>' or '/>'") end
            advance(pos + #name)
            local equals = source:match("^%s*=%s*", pos)
            local value = "true"       -- a bare attribute is a switch: <window closeable>
            if equals then
                advance(pos + #equals)
                local quote = source:sub(pos, pos)
                if quote ~= '"' and quote ~= "'" then fail("attribute " .. name .. ": the value must be in quotes") end
                local close = source:find(quote, pos + 1, true)
                if not close then fail("attribute " .. name .. ": the closing quote is missing") end
                value = decode(source:sub(pos + 1, close - 1))
                advance(close + 1)
            end
            if node.attrs[name] ~= nil then fail("attribute " .. name .. " is given twice in <" .. node.tag .. ">") end
            node.attrs[name] = value
            node.order[#node.order + 1] = name
        end
    end

    local function parseElement()
        local tag = source:match("^<([%w_:.@#%-]+)", pos)
        if not tag then fail("expected a tag name after '<'") end
        local node = { tag = tag:lower(), attrs = {}, order = {}, children = {}, line = line }
        advance(pos + 1 + #tag)
        if parseAttributes(node) then return node end
        local texts = {}
        while true do
            local open = source:find("<", pos, true)
            if not open then fail("<" .. tag .. "> (line " .. node.line .. ") is never closed") end
            if open > pos then texts[#texts + 1] = source:sub(pos, open - 1) end
            advance(open)
            if source:sub(pos, pos + 3) == "<!--" then
                local close = source:find("-->", pos + 4, true)
                if not close then fail("a comment is never closed") end
                advance(close + 3)
            elseif source:sub(pos, pos + 1) == "</" then
                local closing = source:match("^</%s*([%w_:.@#%-]+)%s*>", pos)
                if not closing then fail("a closing tag is malformed") end
                if closing:lower() ~= node.tag then
                    fail("</" .. closing .. "> closes <" .. tag .. "> (line " .. node.line .. ")")
                end
                advance(pos + #source:match("^</%s*[%w_:.@#%-]+%s*>", pos))
                break
            else
                node.children[#node.children + 1] = parseElement()
            end
        end
        node.text = decode(dedent(table.concat(texts)))
        return node
    end

    local ok, result = pcall(function()
        while true do
            local _, afterSpace = source:find("^%s*", pos)
            advance(afterSpace + 1)
            if source:sub(pos, pos + 3) == "<!--" then
                local close = source:find("-->", pos + 4, true)
                if not close then fail("a comment is never closed") end
                advance(close + 3)
            elseif source:sub(pos, pos + 1) == "<?" or source:sub(pos, pos + 1) == "<!" then
                local close = source:find(">", pos, true)
                if not close then fail("a declaration is never closed") end
                advance(close + 1)
            else
                break
            end
        end
        if pos > length then fail("the template is empty") end
        if source:sub(pos, pos) ~= "<" then fail("expected '<'") end
        local root = parseElement()
        local rest = source:sub(pos):gsub("<!%-%-.-%-%->", "")
        if rest:find("%S") then fail("only one root element is allowed") end
        return root
    end)
    if ok then return result end
    if type(result) == "table" and result.xml then return nil, result.xml end
    return nil, "line " .. line .. ": " .. tostring(result)
end
