using GateKit.Symbols.Registry;

namespace GateKit.Symbols.UnitTests;

/// <summary>A Script Extender's table of byte patterns as the binding layer reads it: the bytes of
/// a pattern, the bytes that may be anything, its marks and the symbols it leads to.</summary>
public sealed class MappingsTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("mappings");

    public MappingsTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    string Table(string mappings)
    {
        var path = Path.Combine(root, "BinaryMappings.xml");
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<BinaryMappings>\n  <Mappings Version=\"1\" Default=\"true\">\n" + mappings + "\n  </Mappings>\n</BinaryMappings>\n");
        return path;
    }

    [Fact]
    public void A_pattern_is_its_bytes_with_the_open_ones_marked_and_comments_left_out()
    {
        var read = Mappings.Read(Table("""
                <Mapping Name="a::Start" AllowFail="true">
                  @start 48 89 5c 24 08 // mov [rsp+8],rbx
                  e8 ?? ?? ?? ?? // call 48 8b
                  <Target Type="Absolute" Offset="@start" Symbol="a__Start" />
                </Mapping>
            """));
        var pattern = Assert.Single(read);
        Assert.Equal("a::Start", pattern.Name);
        Assert.Equal(new byte[] { 0x48, 0x89, 0x5c, 0x24, 0x08, 0xe8, 0, 0, 0, 0 }, pattern.Bytes);
        Assert.Equal(new[] { false, false, false, false, false, false, true, true, true, true }, pattern.Wild);
        var target = Assert.Single(pattern.Targets);
        Assert.Equal(("a__Start", "Absolute", 0), (target.Symbol, target.Type, target.Offset));
    }

    [Fact]
    public void A_mark_in_the_middle_gives_its_target_the_offset_of_the_instruction_it_stands_before()
    {
        var read = Mappings.Read(Table("""
                <Mapping Name="uses" Critical="true">
                  48 8b d7 // mov rdx,rdi
                  @ref2 e8 ?? ?? ?? ?? // call a
                  @ref3 48 8b 0d ?? ?? ?? ?? // mov rcx,[rip+x]
                  <Target Type="Indirect" Offset="@ref2" Symbol="ls__A__Free" />
                  <Target Type="Indirect" Offset="@ref3" Symbol="ls__gTable" />
                </Mapping>
            """));
        var pattern = Assert.Single(read);
        Assert.Equal(15, pattern.Bytes.Length);
        Assert.Equal([3, 8], pattern.Targets.Select(t => t.Offset));
        Assert.All(pattern.Targets, t => Assert.Equal("Indirect", t.Type));
    }

    [Fact]
    public void A_mapping_with_no_bytes_or_no_symbol_is_not_a_pattern()
    {
        var read = Mappings.Read(Table("""
                <Mapping Name="empty"><Target Type="Absolute" Offset="0" Symbol="x" /></Mapping>
                <Mapping Name="aimless">48 89 5c</Mapping>
            """));
        Assert.Empty(read);
    }

    [Fact]
    public void The_tables_symbol_and_the_registrys_id_are_one_name_written_two_ways()
    {
        Assert.Equal("eoc::spell::CalculateModifiableSpellRollChance", Mappings.IdOf("eoc__spell__CalculateModifiableSpellRollChance"));
        Assert.Equal("eoc__spell__CalculateModifiableSpellRollChance", Mappings.SymbolOf("eoc::spell::CalculateModifiableSpellRollChance"));
        Assert.Equal("StatusRequestSystemGatherCombatEvents", Mappings.SymbolOf("unplaced::StatusRequestSystemGatherCombatEvents"));
    }
}
