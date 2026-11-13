namespace GateKit.Symbols.Queries;

/// <summary>Tables and indexes of symbols.sqlite, the same text as SCHEMA and INDEXES of the Python
/// tool (symbols.py), so that either tool reads what the other wrote. The `-- comments` are the
/// meanings of the columns (the schema tool of the MCP server shows them).</summary>
internal static class SymbolSchema
{
    public const string Tables = "\n" + """
        CREATE TABLE meta (key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE dlls (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,           -- file name, e.g. EoCClient.dll
            path TEXT NOT NULL,           -- path relative to the Toolkit folder
            size INTEGER, timestamp INTEGER,
            machine TEXT,                 -- x64 / x86 / other
            managed INTEGER,              -- 1 = .NET assembly (has a CLR header)
            exports INTEGER, imports INTEGER,
            note TEXT,                    -- why it was not read, if it was not
            origin TEXT,                  -- toolkit / game
            functions INTEGER             -- functions listed in the exception table (.pdata)
        );
        CREATE TABLE symbols (
            id INTEGER PRIMARY KEY,
            dll_id INTEGER NOT NULL REFERENCES dlls(id),
            ordinal INTEGER,
            rva INTEGER,                  -- NULL for forwarders
            forwarder TEXT,               -- "OTHERDLL.Name" when the export is forwarded
            mangled TEXT,                 -- decorated name as exported; NULL when exported by ordinal only
            full_name TEXT,               -- qualified name without the signature: ecl::Foo::Bar
            namespace TEXT,               -- ecl
            class TEXT,                   -- Foo (Outer::Inner for nested classes); '' for free functions
            method TEXT,                  -- Bar
            signature TEXT,               -- complete undecorated declaration
            access TEXT,                  -- public / protected / private / ''
            kind TEXT                     -- method, function, data, vftable, special, c, ordinal, undecoded
        );
        -- Text constants that code refers to (lea reg, [rip+x]), and the functions that refer to them.
        -- func_rva is the start of the function, taken from the exception table.
        CREATE TABLE strings (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, text TEXT);
        CREATE TABLE string_refs (string_id INTEGER, func_rva INTEGER);
        -- Names of C++ types the engine keeps as text (ls::GetTypeName<T>): ECS components, systems,
        -- queries with the components they read. func_rva is the function that returns the name.
        CREATE TABLE type_names (dll_id INTEGER, string_id INTEGER, type TEXT, kind TEXT, func_rva INTEGER);
        -- Slots of the virtual tables the Toolkit exports. Join symbols on (dll_id, rva) for the method names.
        CREATE TABLE vtable_slots (symbol_id INTEGER, dll_id INTEGER, idx INTEGER, rva INTEGER);
        -- Functions of a Toolkit binary found again in a game executable: both are the only function,
        -- on their side, that refers to `evidence` text constants which are the same on both sides.
        -- method 'strings': see above. method 'calls': found by walking the calls of functions already
        -- matched (the n-th call of one is the n-th call of the other); evidence is the number of
        -- matched callers that agree, sample names one of them.
        -- method 'vtable': the two functions sit in the same slot of a matched pair of virtual tables;
        -- evidence is the number of table pairs that say so, sample names one Toolkit table.
        -- relation compares sizes: 'same' when they are within a factor of three of each other; 'inlined'
        -- when the game function is much larger, which usually means the Toolkit function was inlined
        -- into its caller and the game address is that caller; 'partial' when it is much smaller.
        CREATE TABLE matches (toolkit_dll INTEGER, toolkit_rva INTEGER, game_dll INTEGER, game_rva INTEGER,
                              evidence INTEGER, sample TEXT, method TEXT,
                              toolkit_size INTEGER, game_size INTEGER, relation TEXT);
        -- Direct calls: one row per (caller, callee) pair. callee_dll/callee_rva for a function of one of
        -- the indexed binaries, import for a function of any other DLL (system, third party).
        -- Every function: listed in the exception table, or a leaf function found between listed ones.
        CREATE TABLE functions (dll_id INTEGER, rva INTEGER, size INTEGER, leaf INTEGER);
        -- Parts of split functions: the compiler lists one function as several entries of the exception
        -- table. rva = start of a later part, parent = start of the function. Rows of functions, calls
        -- and xrefs are keyed by parts; queries fold them into the function with this table.
        CREATE TABLE chunks (dll_id INTEGER, rva INTEGER, parent INTEGER);
        -- Virtual tables found by shape (an address code refers to that holds function pointers), in the
        -- game and in the Toolkit, plus the tables the Toolkit exports. symbol_id names the exported ones.
        CREATE TABLE vtabs (id INTEGER PRIMARY KEY, dll_id INTEGER, rva INTEGER, slots INTEGER, symbol_id INTEGER);
        CREATE TABLE vtab_slots (vtab_id INTEGER, idx INTEGER, rva INTEGER);
        -- A Toolkit table recognised in a game executable; support = slots that were already matched.
        CREATE TABLE vtab_matches (toolkit_vtab INTEGER, game_vtab INTEGER, support INTEGER);
        -- Fields of classes, read from the bodies of trivial accessors the Toolkit exports
        -- (GetX / IsX / SetX that only touch [this + offset]). offset is the offset in the Toolkit build;
        -- game_offset is read the same way from the matched function of the game, when there is one.
        CREATE TABLE accessors (
            symbol_id INTEGER, dll_id INTEGER, class TEXT, field TEXT,
            kind TEXT,                    -- get (returns the value), ref (returns its address), set
            offset INTEGER, width INTEGER, type TEXT,
            game_dll INTEGER, game_rva INTEGER, game_offset INTEGER,
            bit INTEGER                   -- for a flag kept as one bit of the field: which bit
        );
        -- Which function of a game executable touches which known global: the index of an ECS type, a
        -- virtual table, a symbol Script Extender locates. kind: r read, w written, a address taken.
        -- Filled only when the capstone package is available at build time.
        CREATE TABLE xrefs (dll_id INTEGER, func_rva INTEGER, target_rva INTEGER, kind TEXT, uses INTEGER);
        CREATE TABLE calls (dll_id INTEGER, caller_rva INTEGER, callee_dll INTEGER, callee_rva INTEGER, import TEXT, sites INTEGER);
        -- What Script Extender's byte patterns (BinaryMappings.xml) locate in a game executable.
        CREATE TABLE se_symbols (dll_id INTEGER, name TEXT, rva INTEGER, mapping TEXT, note TEXT);
        CREATE TABLE se_mappings (dll_id INTEGER, mapping TEXT, matches INTEGER, status TEXT);
        -- Static registration records of the ECS in a game executable: the global that receives the
        -- type's index at start-up, and the context (list) it is registered in.
        CREATE TABLE ecs_ids (dll_id INTEGER, type TEXT, context TEXT, index_rva INTEGER, context_rva INTEGER, source TEXT);
        -- Data layouts from the Script Extender source: structs with their fields.
        CREATE TABLE se_types (
            id INTEGER PRIMARY KEY, name TEXT, namespace TEXT,
            ext_name TEXT,                -- the name Script Extender's Lua API uses
            engine_class TEXT,            -- the engine's own name, as in type_names.type
            kind TEXT,                    -- component, oneframe component, proxy component, tag, boost, system, struct
            base TEXT, file TEXT, line INTEGER, size INTEGER
        );
        CREATE TABLE se_fields (
            type_id INTEGER, idx INTEGER, name TEXT, type TEXT,
            legacy TEXT,                  -- earlier name of the field, often field_<offset>
            hidden INTEGER,               -- not exposed to Lua
            offset INTEGER, size INTEGER, -- computed; NULL where a type of unknown size comes before
            checked INTEGER               -- 1: computed offset equals the offset in the field's name; 2: taken from the name
        );
        CREATE TABLE enums (id INTEGER PRIMARY KEY, name TEXT, namespace TEXT, underlying TEXT, bitmask INTEGER, file TEXT);
        CREATE TABLE enum_values (enum_id INTEGER, name TEXT, value INTEGER);
        CREATE TABLE lua_classes (name TEXT PRIMARY KEY, parent TEXT);
        CREATE TABLE lua_fields (class TEXT, name TEXT, type TEXT);
        CREATE TABLE imports (
            dll_id INTEGER NOT NULL REFERENCES dlls(id),
            from_dll TEXT NOT NULL,       -- name of the DLL the symbol is imported from
            mangled TEXT,                 -- NULL when imported by ordinal
            ordinal INTEGER
        );
        """ + "\n";

    public const string Indexes = "\n" + """
        CREATE INDEX symbols_dll ON symbols(dll_id);
        CREATE INDEX symbols_class ON symbols(class, namespace);
        CREATE INDEX symbols_ns ON symbols(namespace);
        CREATE INDEX symbols_method ON symbols(method);
        CREATE INDEX symbols_mangled ON symbols(mangled);
        CREATE INDEX imports_mangled ON imports(mangled);
        CREATE INDEX imports_dll ON imports(dll_id);
        CREATE INDEX se_symbols_name ON se_symbols(name);
        CREATE INDEX ecs_ids_type ON ecs_ids(type);
        CREATE INDEX se_types_name ON se_types(name);
        CREATE INDEX se_types_engine ON se_types(engine_class);
        CREATE INDEX se_fields_type ON se_fields(type_id, idx);
        CREATE INDEX se_fields_name ON se_fields(name);
        CREATE INDEX enums_name ON enums(name);
        CREATE INDEX enum_values_enum ON enum_values(enum_id);
        CREATE INDEX enum_values_name ON enum_values(name);
        CREATE INDEX lua_fields_class ON lua_fields(class);
        CREATE INDEX symbols_rva ON symbols(dll_id, rva);
        CREATE INDEX strings_dll ON strings(dll_id, rva);
        CREATE INDEX string_refs_string ON string_refs(string_id);
        CREATE INDEX string_refs_func ON string_refs(func_rva);
        CREATE INDEX type_names_type ON type_names(type);
        CREATE INDEX vtable_slots_symbol ON vtable_slots(symbol_id, idx);
        CREATE INDEX vtable_slots_rva ON vtable_slots(dll_id, rva);
        CREATE INDEX matches_toolkit ON matches(toolkit_dll, toolkit_rva);
        CREATE INDEX matches_game ON matches(game_dll, game_rva);
        CREATE INDEX calls_caller ON calls(dll_id, caller_rva);
        CREATE INDEX xrefs_target ON xrefs(dll_id, target_rva);
        CREATE INDEX xrefs_func ON xrefs(dll_id, func_rva);
        CREATE INDEX accessors_class ON accessors(class);
        CREATE INDEX accessors_field ON accessors(field);
        CREATE UNIQUE INDEX functions_rva ON functions(dll_id, rva);
        CREATE UNIQUE INDEX chunks_rva ON chunks(dll_id, rva);
        CREATE INDEX chunks_parent ON chunks(dll_id, parent);
        CREATE INDEX vtabs_rva ON vtabs(dll_id, rva);
        CREATE INDEX vtabs_symbol ON vtabs(symbol_id);
        CREATE INDEX vtab_slots_vtab ON vtab_slots(vtab_id, idx);
        CREATE INDEX vtab_slots_rva ON vtab_slots(rva);
        CREATE INDEX vtab_matches_toolkit ON vtab_matches(toolkit_vtab);
        CREATE INDEX vtab_matches_game ON vtab_matches(game_vtab);
        CREATE INDEX calls_callee ON calls(callee_dll, callee_rva);
        CREATE INDEX calls_import ON calls(import) WHERE import IS NOT NULL;
        """ + "\n";
}
