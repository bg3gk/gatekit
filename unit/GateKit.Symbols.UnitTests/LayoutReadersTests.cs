using GateKit.Symbols.Decomp;

namespace GateKit.Symbols.UnitTests;

/// <summary>The readers of layouts read the registry's types first, the layout files only without them.</summary>
public sealed class LayoutReadersTests : IDisposable
{
    readonly string root = GateKit.Testing.Scratch.NewPath("layout-readers");

    public void Dispose()
    {
        try { Directory.Delete(root, true); }
        catch (IOException) { }
    }

    void Type(string folder, string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(root, "registry", "types", folder));
        File.WriteAllText(Path.Combine(root, "registry", "types", folder, name + ".json"), json);
    }

    [Fact]
    public void A_record_with_the_registry_beside_it_is_read_from_the_registry()
    {
        Type("toy", "Widget", """
            {"schema": "registry/type/1", "id": "toy::Widget", "kind": "struct", "size": 8,
             "fields": [{"offset": 0, "name": "Count", "type": "int32_t", "rung": "inferred"}], "concept": {"kind": "component"}, "survey": {"batch": "alive"}}
            """);
        Type("toy", "Stream", """{"schema": "registry/type/1", "id": "toy::Stream", "kind": "struct", "fields": [{"offset": 0, "name": "Seed", "type": "uint64_t"}]}""");
        Directory.CreateDirectory(Path.Combine(root, "layouts", "old"));
        File.WriteAllText(Path.Combine(root, "layouts", "old", "toy.Old.json"), """{"component": "toy::Old", "size": 4, "fields": [{"name": "A", "offset": 0, "type": "int32_t"}]}""");

        Assert.Equal(Path.GetFullPath(root), RecordTypes.FindCoverage(root));
        // the structures of the record: the registry's types made from layouts, the layout files not read
        var types = RecordTypes.Read(root);
        Assert.Equal(["toy::Widget"], types.Keys);
        Assert.Equal("toy::Widget\t8\t0\t4\tint\tCount\tint32_t\n", types["toy::Widget"].Lines());
        // a layout exists for every type of the registry, and for a layout file still there
        Assert.Equal(["toy::Old", "toy::Stream", "toy::Widget"], GateKit.Symbols.Queries.SeLayouts.Existing(root).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_folder_with_neither_a_registry_nor_layouts_is_no_record()
    {
        Directory.CreateDirectory(root);
        Assert.Throws<SymbolsException>(() => RecordTypes.FindCoverage(root));
    }
}
