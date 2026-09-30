using System.Linq;
using System.Text.RegularExpressions;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Guard.Tests;

/// <summary>
/// D14's success criterion, made mechanical: <b>the registered per-element vocabulary and the
/// implemented one are the same set.</b>
///
/// <para>D14 was filed as "72 per-element parry/block/reflect channels have no reader in any mode" and
/// read as dead vocabulary to delete. It was the reverse — three families were missing the element term
/// the other nine had — so the module closed it by implementing the reading. This test is what stops
/// the gap reopening: a new per-element channel factory with no reader is the same defect again, and
/// the next person to add one finds out here rather than in an audit two months later.</para>
///
/// <para>⚠️ It asserts <b>closure</b>, never a count. The number of per-element families is content
/// that grows; what must hold is that none of them is unread.</para>
/// </summary>
[Trait("VerificationId", "guard.per-element-closure")]
public sealed class PerElementChannelClosureTests
{
    static string RepoRoot()
    {
        return KeepverseRoots.Core();
    }

    static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    /// <summary>Every `public static string Xxx(ElementTypeId ...)` factory the channel registry declares.</summary>
    static string[] PerElementFactories() =>
        Regex.Matches(
                Read("src", "FusionRpg.Core", "Stats", "Derived", "DerivedStatChannels.cs"),
                @"public static string ([A-Za-z]+)\(ElementTypeId")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

    [Fact]
    public void There_is_a_per_element_vocabulary_to_check()
    {
        // Liveness: a regex that matched nothing would make every assertion below vacuous.
        Assert.NotEmpty(PerElementFactories());
    }

    [Fact]
    public void Every_per_element_channel_factory_has_a_reader()
    {
        var reader = Read("src", "FusionRpg.Core", "Combat", "CombatDerivedReader.cs");

        var unread = PerElementFactories()
            .Where(f => !reader.Contains($"DerivedStatChannels.{f}(", StringComparison.Ordinal))
            .ToArray();

        Assert.True(unread.Length == 0,
            "registered per-element channels with no reader — this is D14 reopening: " +
            string.Join(", ", unread));
    }

    /// <summary>
    /// And each reader ADDS its element half to the omni one rather than replacing it. The additive
    /// rule is what makes omni anti-one-trick baseline protection instead of a multiplier on the
    /// specialisation the actor already paid for, and it is enforced here at the declaration so a new
    /// family cannot quietly pick the other shape.
    /// </summary>
    [Fact]
    public void Every_per_element_read_is_an_addition_to_the_omni_half()
    {
        var reader = Read("src", "FusionRpg.Core", "Combat", "CombatDerivedReader.cs");

        foreach (var factory in PerElementFactories())
        {
            // The omni half and the element half in ONE statement, joined by `+`. Scoped to a single
            // statement (no `;` between them) so this cannot match two unrelated lines, and tolerant of
            // what sits between: the shield families take a NULLABLE element and spell the same
            // addition as `omni + (element is { } e ? snap.Get(...) : 0)`, which is additive in a
            // different shape rather than a different rule. A `*` between them would be the snowball
            // rule this repo refuses, and a lone element read would be substitution — neither matches.
            var additive = Regex.IsMatch(reader,
                $@"Omni\)\s*\+\s*[^;]{{0,160}}?DerivedStatChannels\.{Regex.Escape(factory)}\(",
                RegexOptions.Singleline);

            Assert.True(additive,
                $"{factory} is read, but not as `omni + element` — check CombatDerivedReader");
        }
    }
}
