using System.Text.Json;
using FusionRpg.TestSupport;
using Xunit;

namespace FusionRpg.Core.Tests.Combat.Element;

/// <summary>
/// combat-math-dedup audit §2.1c/§4. The `combat` and `shield` blocks in
/// `gk-data/packs/fusion/data/seed/elements/matrices.json` are duplicated <b>on purpose</b>: the two tables are
/// independently editable, and diverging them is an Ask-first balance decision. What the file lacked
/// was a way to tell an intended divergence from a typo.
///
/// <para>This asserts the <b>contract</b>, never the content (the `validation-ssot.md` rule): either
/// the two blocks are identical, or the file carries an explicit <c>_divergence</c> note naming the
/// decision. A plain parity test would be wrong — it would fail the day a balance pass legitimately
/// diverges the shield matrix, and the "fix" would be to delete the test. Here, diverging costs one
/// line of intent; a typo fails the comparison.</para>
/// </summary>
public class ElementMatrixSeedDivergenceTests
{
    static string SeedPath =>
        Path.Combine(ContentRoot.Path, "data", "seed", "elements", "matrices.json");

    [Fact]
    public void The_combat_and_shield_blocks_are_identical_or_carry_an_explicit_divergence_note()
    {
        Assert.True(File.Exists(SeedPath), "missing " + SeedPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(SeedPath));
        var root = doc.RootElement;
        var entries = root.GetProperty("entries").EnumerateArray().ToArray();

        var combat = Rows(entries, "combat");
        var shield = Rows(entries, "shield");

        // Non-vacuity: both blocks are present and the same width, so a missing or short block cannot
        // make the comparison below pass by being empty on both sides.
        Assert.NotEmpty(combat);
        Assert.Equal(combat.Count, shield.Count);

        if (combat.SetEquals(shield)) return; // the contract's first arm

        // The second arm: an explicit, non-empty `_divergence` note on the file.
        Assert.True(
            root.TryGetProperty("_divergence", out var note) &&
            note.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(note.GetString()),
            "the combat and shield matrix blocks differ with no explicit `_divergence` note — an "
            + "intended divergence costs one line of intent; a typo fails here");
    }

    /// <summary>One block as a set of `attacker|defender|unit` keys — the row identity, with the
    /// block name itself excluded (that is the only field the two blocks differ in by construction).</summary>
    static HashSet<string> Rows(IEnumerable<JsonElement> entries, string matrix) =>
        entries
            .Where(e => string.Equals(e.GetProperty("matrix").GetString(), matrix, StringComparison.Ordinal))
            .Select(e =>
                $"{e.GetProperty("attacker").GetString()}|{e.GetProperty("defender").GetString()}|{e.GetProperty("unit").GetInt32()}")
            .ToHashSet(StringComparer.Ordinal);
}
