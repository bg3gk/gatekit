using GateKit.GameData.Resources;
using LSLib.LS;
using static GateKit.GameData.Tests.Synthetic;

namespace GateKit.GameData.Tests;

public class ReaderTests
{
    private const string Actor = "11111111-2222-3333-4444-555555555555";

    private static T Read<T>(Resource resource, bool lsx, T reader) where T : ResourceReader
    {
        using var stream = new MemoryStream(lsx ? Lsx(resource) : Lsf(resource));
        ResourceEvents.Read(stream, lsx, reader);
        return reader;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TimelineTracksActorsAndKeys(bool lsx)
    {
        var data = Read(Timeline(Actor), lsx, new TimelineReader()).Result();
        Assert.Equal(10.0, data.Duration);
        Assert.Equal([(10.0, "node-1")], data.Phases.Select(p => (p.Duration!.Value, p.DialogNode)).ToList());
        Assert.Equal(0, data.Speakers[Actor]);
        Assert.Equal("character", data.Actors.Single().Data["ActorTypeId"]);
        Assert.Equal(2, data.Tracks.Count);

        var weapon = data.Tracks[0];
        Assert.Equal("TLShowWeapon", weapon.Type);
        Assert.Equal(Actor, weapon.Actor);
        Assert.Equal((1, 1), (weapon.Hides!.Value, weapon.Shows!.Value));
        Assert.Equal(2, weapon.Keys);
        Assert.Equal(["False", "True"], weapon.KeyList!.Select(k => k.Value).ToList());
        Assert.Equal([1.0, 2.5], weapon.KeyList!.Select(k => k.Time).ToList());
        Assert.Equal("{\"InterpolationType\": \"3\", \"ShowWeapon\": \"False\", \"Time\": \"1\"}", weapon.KeyList![0].Attrs);

        var animation = data.Tracks[1];
        Assert.Null(animation.KeyList);            // keys of other tracks are only counted
        Assert.Equal(1, animation.Keys);
        Assert.Null(animation.Hides);
    }

    [Fact]
    public void AllKeysKeepsEveryTrack()
    {
        var data = Read(Timeline(Actor), false, new TimelineReader(allKeys: true)).Result();
        Assert.Single(data.Tracks[1].KeyList!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DialogFlow(bool lsx)
    {
        var d = Read(Dialog("d-uuid", "t-uuid", Actor), lsx, new DialogReader()).Result();
        Assert.Equal(("d-uuid", "Test", "t-uuid"), (d.Uuid, d.Category, d.Timeline));
        Assert.Equal([(0, Actor, "map")], d.Speakers.Select(s => (s.Index, s.Object, s.Mapping)).ToList());
        Assert.Equal([("node-1", "TagGreeting", (int?)0), ("node-2", "TagAnswer", null)], d.Nodes.Select(n => (n.Uuid, n.Constructor, n.Speaker)).ToList());
        Assert.Equal(2, d.Lines.Count);
        Assert.Equal(("node-1", 0, "node-2"), (d.Links[0].Node, d.Links[0].Index, d.Links[0].Child));
        Assert.Equal(("node-1", "check", "Global", "flag-1", 1), (d.Flags[0].Node, d.Flags[0].Kind, d.Flags[0].Type, d.Flags[0].Flag, d.Flags[0].Value));
        Assert.Contains(("node-1", "Root", "True"), d.Attrs.Select(a => (a.Node, a.Name, a.Value)));
        Assert.Contains(("node-2", "endnode", "True"), d.Attrs.Select(a => (a.Node, a.Name, a.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenericObjectsWithNestedAttributes(bool lsx)
    {
        var res = Res(N("Templates",
            N("GameObjects", ("MapKey", "aaaa"), ("Name", "Sword"), ("Type", "item"),
                N("Bounds", N("Bound", ("Height", 2f)))),
            N("GameObjects", ("MapKey", "bbbb"), ("Name", "Shield"))));
        var (region, objects) = Read(res, lsx, new GenericReader()).Result();
        Assert.Equal("Templates", region);
        Assert.Equal(2, objects.Count);
        Assert.Equal(("GameObjects", "aaaa", "Sword"), (objects[0].Node, objects[0].Key, objects[0].Name));
        Assert.Contains(("Bounds/Bound.Height", "2"), objects[0].Attrs);
        Assert.False(objects[0].Truncated);
    }

    [Fact]
    public void GenericObjectCutsLongNesting()
    {
        var children = Enumerable.Range(0, GenericReader.NestedLimit + 5).Select(i => (object)N("Child", ("Value", i))).ToArray();
        var res = Res(N("Root", N("Thing", new object[] { ("Name", "Big"), N("List", children) })));
        var (_, objects) = Read(res, false, new GenericReader()).Result();
        Assert.True(objects[0].Truncated);
        Assert.Equal(GenericReader.NestedLimit + 1, objects[0].Attrs.Count);
    }

    [Fact]
    public void FlatReaderTakesDirectAttributesAtDepth()
    {
        var res = Res(N("Templates", N("GameObjects", ("MapKey", "k1"), ("Name", "One"), ("Unrelated", "x"), N("Inner", ("Name", "no")))));
        var rows = Read(res, false, FlatReader.Templates()).Result();
        Assert.Equal("k1", rows.Single()["MapKey"]);
        Assert.Equal("One", rows.Single()["Name"]);
        Assert.False(rows.Single().ContainsKey("Unrelated"));
    }

    [Fact]
    public void TranslatedStringIsItsHandle()
    {
        var res = Res(N("Templates", N("GameObjects", ("MapKey", "k1"),
            ("DisplayName", new TranslatedString { Handle = "h12345678g1234g1234g1234g123456789012", Version = 1 }))));
        var rows = Read(res, false, FlatReader.Templates()).Result();
        Assert.Equal("h12345678g1234g1234g1234g123456789012", rows.Single()["DisplayName"]);
    }

    [Fact]
    public void DialogLineWithoutTextIsItsHandle()
    {
        var res = Res(N("dialog", N("nodes", N("node", ("UUID", "n1"),
            N("TaggedTexts", N("TaggedText", N("TagTexts", N("TagText",
                ("TagText", new TranslatedFSString { Handle = "h00000003g0000g0000g0000g000000000003", Version = 1, Arguments = [] })))))))));
        var d = Read(res, false, new DialogReader()).Result();
        Assert.Equal("h00000003g0000g0000g0000g000000000003", d.Lines.Single().Handle);
    }

    [Fact]
    public void GuidsAreWrittenAsTheyAppearInLsx()
    {
        var guid = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
        var res = Res(N("Root", N("Thing", ("UUID", guid))));
        var (_, fromLsf) = Read(res, false, new GenericReader()).Result();
        var (_, fromLsx) = Read(res, true, new GenericReader()).Result();
        Assert.Equal(guid.ToString(), fromLsf[0].Key);
        Assert.Equal(fromLsx[0].Key, fromLsf[0].Key);
    }
}
