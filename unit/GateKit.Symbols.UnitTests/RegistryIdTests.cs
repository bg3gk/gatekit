using GateKit.Symbols.Registry;

namespace GateKit.Symbols.UnitTests;

/// <summary>The id of a registry entry and the one rule that turns it into a file and back.</summary>
public sealed class RegistryIdTests
{
    [Theory]
    [InlineData("esv::RollSystem::RollDice", "esv/RollSystem/RollDice.json")]
    [InlineData("eoc::HealthComponent", "eoc/HealthComponent.json")]
    [InlineData("unplaced::DecideAttackHit", "unplaced/DecideAttackHit.json")]
    [InlineData("ls::Array<int>::Add", "ls/Array[int]/Add.json")]
    [InlineData("ecs::SystemUpdate<esv::roll::stream::UpdateSystem>", "ecs/SystemUpdate[esv..roll..stream..UpdateSystem].json")]
    [InlineData("ls::Map<ls::FixedString, int*>::Find", "ls/Map[ls..FixedString,+int^]/Find.json")]
    [InlineData("ecl::Transform::SetTranslate(Vector3f*)", "ecl/Transform/SetTranslate(Vector3f^).json")]
    [InlineData("esv::RollSystem::RollDice@thunk", "esv/RollSystem/RollDice@thunk.json")]
    [InlineData("esv::RollSystem::unnamed_3f2a9c1d", "esv/RollSystem/unnamed_3f2a9c1d.json")]
    [InlineData("ls::Vector::operator[]", "ls/Vector/operator%5B%5D.json")]
    [InlineData("ls::Vector::operator<<", "ls/Vector/operator[[.json")]
    [InlineData("ls::Vector::operator->", "ls/Vector/operator-].json")]
    [InlineData("ls::Vector::~Vector", "ls/Vector/~Vector.json")]
    [InlineData("ns::CON", "ns/%43ON.json")]
    [InlineData("ns::a.b", null)]
    public void An_id_is_written_as_a_path_and_read_back_as_itself(string id, string? path)
    {
        if (path is null)
        {
            Assert.Throws<RegistryException>(() => RegistryId.ToPath(id));     // a member lives in its entity's file
            return;
        }
        Assert.Equal(path, RegistryId.ToPath(id));
        Assert.Equal(id, RegistryId.FromPath(path));
    }

    [Theory]
    [InlineData("eoc::HealthComponent.Hp", "eoc::HealthComponent", "Hp")]
    [InlineData("esv::RollSystem::RollDice.total", "esv::RollSystem::RollDice", "total")]
    [InlineData("ls::Array<a::b>::Add@thunk.x", "ls::Array<a::b>::Add@thunk", "x")]
    [InlineData("ns::F(float, int).count", "ns::F(float, int)", "count")]
    public void A_member_is_reached_with_a_dot_after_its_entity(string id, string entity, string member)
    {
        var parts = RegistryId.Parse(id);
        Assert.Equal(entity, parts.Entity);
        Assert.Equal(member, parts.Member);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a::")]
    [InlineData("::a")]
    [InlineData("a::::b")]
    [InlineData("a b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a@")]
    [InlineData("a.")]
    [InlineData("a.b c")]
    public void A_malformed_id_is_refused(string id) => Assert.False(RegistryId.IsValid(id));

    [Fact]
    public void Every_character_survives_the_path_and_two_ids_differing_by_case_share_a_case_key()
    {
        foreach (var id in new[] { "n::A<b::c, d e>::f%g", "n::Ünïcode", "n::x<1:2>", "n::q?r", "n::t<a|b>", "n::p<1.5>" })
        {
            var path = RegistryId.ToPath(id);
            Assert.DoesNotContain(path.Split('/'), part => part.IndexOfAny(['<', '>', '*', ':', '?', '"', '|', '\\', ' ']) >= 0);
            Assert.Equal(id, RegistryId.FromPath(path));
        }
        Assert.Equal(RegistryId.CaseKey("eoc::Foo"), RegistryId.CaseKey("eoc::FOO"));
        Assert.NotEqual(RegistryId.ToPath("eoc::Foo"), RegistryId.ToPath("eoc::FOO"));
    }

    [Fact]
    public void A_thing_without_a_name_or_an_owner_has_a_place()
    {
        Assert.Equal("esv::RollSystem::unnamed_3f2a9c1d", RegistryId.Unnamed("esv::RollSystem", "3f2a9c1d"));
        Assert.Equal("unplaced::unnamed_00ff00ff", RegistryId.Unnamed(null, "00ff00ff"));
        Assert.True(RegistryId.IsUnnamed("unplaced::unnamed_00ff00ff"));
        Assert.Equal("unplaced::RollDamageDice", RegistryId.Placed("RollDamageDice"));
        Assert.Equal("eoc::X", RegistryId.Placed("eoc::X"));
        Assert.Equal("RollDamageDice", RegistryId.Unplace("unplaced::RollDamageDice"));
    }

    [Fact]
    public void A_path_too_long_for_every_system_is_refused()
    {
        var id = "ns::" + new string('a', RegistryId.MaxRelativePath);
        Assert.Throws<RegistryException>(() => RegistryId.ToPath(id));
    }
}
