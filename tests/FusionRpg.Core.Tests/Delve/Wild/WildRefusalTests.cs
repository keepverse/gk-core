using System.Reflection;
using FusionRpg.Core.Delve.Wild;
using FusionRpg.Core.Battle.Capture;
using Xunit;

namespace FusionRpg.Core.Tests.Delve.Wild;

/// <summary>D4.8 (spec-wild-room.md §9) — `WildRefusal`'s own eleven ids, plus the cross-check that
/// the other three (declared in D3.13/D3.30, D4.6, D4.7) still exist where this file's own doc
/// comment says they do, so a future rename there cannot silently orphan this file's cross-reference.</summary>
public class WildRefusalTests
{
    static IEnumerable<string> AllConstants() =>
        typeof(WildRefusal).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!);

    [Fact]
    public void There_are_exactly_eleven_new_refusal_ids()
    {
        Assert.Equal(11, AllConstants().Count());
    }

    [Fact]
    public void Every_id_is_unique()
    {
        var all = AllConstants().ToList();
        Assert.Equal(all.Count, all.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [MemberData(nameof(EachConstant))]
    public void Every_id_is_kebab_case_with_a_dotted_namespace_prefix(string id)
    {
        Assert.Matches("^[a-z]+\\.[a-z][a-z-]*$", id);
    }

    public static IEnumerable<object[]> EachConstant() => AllConstants().Select(id => new object[] { id });

    [Fact]
    public void The_three_refusals_declared_elsewhere_still_exist_where_this_files_own_comment_says()
    {
        // This file's own doc comment names these three as living beside their raising logic rather
        // than here -- proven, not just claimed, so a rename at either site fails loudly.
        Assert.Equal("delve.price-undesigned", FusionRpg.Core.Delve.Loot.DelvePriceRules.PriceUndesigned);
        Assert.Equal("capture.not-landed", CaptureRefusal.NotLanded);
        Assert.Equal("altar.banner-unknown", AltarRefusal.BannerUnknown);
    }
}
