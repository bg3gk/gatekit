using System.Text;
using GateKit.Names;
using GateKit.Symbols.Registry;

namespace GateKit.UnitTests;

/// <summary>`gatekit names` once the layouts are in the registry: it reads and writes the registry's
/// type entries, and the layout files it came from are left as they were.</summary>
public sealed class RegistryNamesTests : IDisposable
{
    readonly string record = GateKit.Testing.Scratch.NewPath("registry-names");

    public void Dispose()
    {
        if (Directory.Exists(record)) Directory.Delete(record, true);
    }

    [Fact]
    public void A_name_set_after_the_import_goes_into_the_registry_and_the_tally_reads_it_from_there()
    {
        var folder = Path.Combine(record, "layouts", "alive");
        Directory.CreateDirectory(folder);
        var layout = Path.Combine(folder, "esv.lock.A.json");
        File.WriteAllText(layout, """
            {
              "component": "esv::lock::A",
              "ext_name": "X",
              "size": 8,
              "status": "confirmed",
              "fields": [{"name": "field_0", "offset": 0, "type": "uint32_t", "source": "unknown", "evidence": "seen"}, {"name": "field_4", "offset": 4, "type": "uint32_t", "source": "unknown", "evidence": "seen"}]
            }
            """.ReplaceLineEndings("\n"), new UTF8Encoding(false));
        var before = File.ReadAllBytes(layout);
        var store = RegistryStore.Of(record);
        Assert.Equal(1, RegistryImport.Layouts(store, record, false, false, TextWriter.Null).RoundTrip);

        var written = FieldNames.Apply(record, [new NameChange("esv::lock::A", 4, "Timer", "inferred", "counts down in the lock system's update")]);
        Assert.Equal("field_4", Assert.Single(written).Was);
        Assert.Equal(before, File.ReadAllBytes(layout));
        var entry = store.Get("esv::lock::A")!.Json;
        Assert.Equal("Timer", (string?)entry["fields"]![1]!["name"]);
        Assert.Equal("inferred", (string?)entry["fields"]![1]!["rung"]);
        Assert.Equal("alive", (string?)entry["survey"]!["batch"]);
        Assert.Equal(1, Tally.Measure(LayoutStore.All(record)).Named);

        // a sentence that names the work is refused by the writer, and nothing is written
        Assert.Throws<NamesException>(() => FieldNames.Apply(record, [new NameChange("esv::lock::A", 0, "Owner", "inferred", "found by the coordinator")]));
        Assert.Equal("field_0", (string?)store.Get("esv::lock::A")!.Json["fields"]![0]!["name"]);
    }
}
