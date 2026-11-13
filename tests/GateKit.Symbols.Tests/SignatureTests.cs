using GateKit.Symbols.Queries;

namespace GateKit.Symbols.Tests;

/// <summary>Byte signatures on hand-written x64 code: what is masked, how it prints, how matches are counted.</summary>
public class SignatureTests
{
    static readonly byte[] Code =
    [
        0x48, 0x89, 0x5c, 0x24, 0x08,               // mov qword ptr [rsp + 8], rbx
        0xe8, 0x10, 0x00, 0x00, 0x00,               // call rel32
        0x48, 0x8d, 0x05, 0x20, 0x00, 0x00, 0x00,   // lea rax, [rip + 0x20]
        0x74, 0x05,                                 // je short: part of the shape
        0xff, 0x15, 0x30, 0x00, 0x00, 0x00,         // call qword ptr [rip + 0x30]
        0xc3,                                       // ret: the end
        0x90, 0x90,
    ];

    [Fact]
    public void MasksPlacementDependentBytes()
    {
        var items = Signature.Masked(Code, 0x1000);
        Assert.Equal(6, items.Count);
        Assert.All(items[0].Mask, m => Assert.Equal(0xFF, m));
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0, 0 }, items[1].Mask);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 }, items[2].Mask);
        Assert.Equal(new byte[] { 0xFF, 0xFF }, items[3].Mask);
        Assert.Equal(new byte[] { 0xFF, 0xFF, 0, 0, 0, 0 }, items[4].Mask);
        Assert.Equal("mov qword ptr [rsp + 8], rbx", items[0].Text);
        Assert.Equal("call 0x101a", items[1].Text);
        Assert.Equal("lea rax, [rip + 0x20]", items[2].Text);
        Assert.Equal("ret", items[5].Text);
    }

    [Fact]
    public void RendersAMapping()
    {
        var text = Signature.Render("toy::Widget::Update", Signature.Masked(Code, 0x1000).Take(3).ToList());
        Assert.Equal(string.Join("\n",
            "<Mapping Name=\"toy::Widget::Update\">",
            "    @start 48 89 5c 24 08 // mov qword ptr [rsp + 8], rbx",
            "    e8 ?? ?? ?? ?? // call 0x101a",
            "    48 8d 05 ?? ?? ?? ?? // lea rax, [rip + 0x20]",
            "    <Target Type=\"Absolute\" Offset=\"@start\" Symbol=\"toy__Widget__Update\" />",
            "</Mapping>"), text);
    }

    [Fact]
    public void CountsWithoutOverlaps()
    {
        byte[] data = [0xAA, 0xAA, 0xAA, 0x00, 0xAA, 0x01, 0xCC, 0xAA, 0x02, 0xCC];
        Assert.Equal(1, Signature.CountIn(data, 0, 4, [0xAA, 0xAA], [0xFF, 0xFF], 3));
        Assert.Equal(2, Signature.CountIn(data, 0, data.Length, [0xAA, 0x00, 0xCC], [0xFF, 0x00, 0xFF], 3));
        Assert.Equal(1, Signature.CountIn(data, 0, data.Length, [0xAA, 0x00, 0xCC], [0xFF, 0x00, 0xFF], 1));
        Assert.Equal(0, Signature.CountIn(data, 0, 6, [0xAA, 0x00, 0xCC], [0xFF, 0x00, 0xFF], 3));
    }
}
