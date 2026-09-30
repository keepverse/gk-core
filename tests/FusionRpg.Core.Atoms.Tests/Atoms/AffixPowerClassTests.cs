using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Items;
using FusionRpg.TestSupport;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// EPL1.1 acceptance (`docs/architecture/effect-pipeline/spec-affix-power-class.md`, module 11) —
/// the L0 classification half's closed structural vocabulary: the enum, its checked-in registry
/// (`gk-data/packs/fusion/data/seed/items/_registry/power-classes.v1.json`), and the mirror agreement between them.
///
/// <para><b>What this pins, and why a literal is right here.</b> The roster is a CLOSED vocabulary
/// (spec "Boundaries": *ask first* before adding a sixth class, exactly as the 12 atom kinds and 8
/// triggers are), so "five classes, consecutive ordinals 0..4" is its contract, not a population
/// reading — `docs/architecture/validation-ssot.md` §6 says so in as many words. What this does
/// NOT pin is any family, affix or corpus count: the classification run is EPL1.3 under its own
/// owner charter, and a coverage number is a reading that moves when content ships.</para>
///
/// <para><b>Identity only.</b> Nothing in this vocabulary is a number a balance pass would move. The
/// ordinal is structural metadata (it is what `MAX` over a bundle's members orders by), and the
/// spec's share column is a tuning TARGET that lives in <c>gk-core/data/tuning/</c>, never in the registry
/// or the mirror — P1: <i>the LLM writes identity, deterministic code writes magnitude</i>.</para>
///
/// <para><b>Closed, not defaulted.</b> An unknown id is a loud failure. There is deliberately no
/// <c>TryParse</c>-to-<see cref="AffixPowerClass.Filler"/> path anywhere in this slice: a silent
/// default would drop the strongest unclassified effect into the cheapest pool, which the spec
/// calls the worst possible outcome.</para>
/// </summary>
public class AffixPowerClassTests
{
    /// <summary>The registry file, read through the ordinary content root — the same resolution
    /// every other data-reading test here uses, so this proves the SHIPPED file, not a fixture.</summary>
    static string RegistryJson() =>
        File.ReadAllText(Path.Combine(KeepverseRoots.Content(), "data", "seed", "items", "_registry", "power-classes.v1.json"));

    [Fact]
    public void The_vocabulary_is_five_classes_with_consecutive_ordinals_from_zero()
    {
        // Structural (tunables-ssot.md T2): a closed-vocabulary cardinality, not a balance dial.
        // The spec corrected its own earlier "spaced by 10" claim on 2026-09-03 — no roster in this
        // codebase spaces, and the invented precedent cannot be the argument.
        Assert.Equal(5, AffixPowerClassIds.Count);

        var values = Enum.GetValues<AffixPowerClass>();
        Assert.Equal(AffixPowerClassIds.Count, values.Length);
        for (var i = 0; i < values.Length; i++)
            Assert.Equal(i, (int)values[i]); // consecutive, 0..4, no gaps and no renumbering

        // Every declared member is a value the parser can hand back — `All` is derived from
        // `Enum.GetValues`, so a member missing from either side is a build/shape defect, not a
        // silent omission.
        Assert.Equal(
            new[] { "filler", "notable", "potent", "defining", "pinnacle" },
            AffixPowerClassIds.All);
    }

    [Fact]
    public void Every_class_name_parses_and_round_trips_back_to_itself()
    {
        foreach (var id in AffixPowerClassIds.All)
        {
            Assert.True(AffixPowerClassIds.TryParse(id, out var parsed), $"'{id}' did not parse");
            Assert.Equal(id, AffixPowerClassIds.IdOf(parsed));
            Assert.Equal(parsed, AffixPowerClassIds.Parse(id));
        }
    }

    [Fact]
    public void An_unknown_id_is_rejected_never_coerced_to_filler()
    {
        // The closedness contract. Note the "Filler" spelling: ids are bare lowercase words, and a
        // case-folding parse would make a mis-cased authored value resolve to a class instead of
        // reporting it -- which is how a vocabulary silently grows a second spelling of itself.
        foreach (var unknown in new[] { "legendary", "Filler", "FILLER", "filler ", " filler", "", "fill" })
        {
            Assert.False(AffixPowerClassIds.TryParse(unknown, out _), $"'{unknown}' was accepted");
            Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassIds.Parse(unknown));
        }

        Assert.False(AffixPowerClassIds.TryParse(null, out _));
        Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassIds.Parse(null));

        // And an out-of-range cast is not a class either: `(AffixPowerClass)99` is not silently
        // usable, because `IsDefined` is the only thing that decides membership.
        Assert.False(AffixPowerClassIds.IsDefined((AffixPowerClass)99));
        Assert.False(AffixPowerClassIds.IsDefined((AffixPowerClass)(-1)));
        Assert.Equal(-1, AffixPowerClassIds.OrdinalOf((AffixPowerClass)99));
        Assert.Equal(-1, AffixPowerClassIds.OrdinalOf((AffixPowerClass)(-1)));
    }

    [Fact]
    public void The_checked_in_registry_and_the_mirror_declare_identical_ids_and_ordinals()
    {
        var rows = AffixPowerClassRegistry.Parse(RegistryJson());

        Assert.Equal(AffixPowerClassIds.All, rows.Select(r => r.Id));
        Assert.Equal(rows.Select(r => (int)r.Value), rows.Select(r => r.Ordinal));
        Assert.Equal(Enumerable.Range(0, AffixPowerClassIds.Count), rows.Select(r => r.Ordinal));

        // The parser is the mirror's own check: if the file drifts from the enum, the ids disagree
        // here and the parse refuses rather than a downstream consumer reading a class the enum
        // cannot name.
        foreach (var row in rows)
            Assert.Equal(row.Ordinal, (int)row.Value);
    }

    [Fact]
    public void A_registry_that_disagrees_with_the_mirror_is_refused()
    {
        // Proof the equality above is enforced by the parser and not merely observed by a test.
        var json = RegistryJson()
            .Replace("\"pinnacle\"", "\"apex\"", StringComparison.Ordinal);

        var ex = Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassRegistry.Parse(json));
        Assert.Contains("apex", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_registry_with_an_invalid_schema_or_a_smuggled_number_is_refused()
    {
        var unsupportedSchema = RegistryJson()
            .Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2", StringComparison.Ordinal);
        Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassRegistry.Parse(unsupportedSchema));

        var notAppendOnly = RegistryJson()
            .Replace("\"appendOnly\": true", "\"appendOnly\": false", StringComparison.Ordinal);
        Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassRegistry.Parse(notAppendOnly));

        var smuggledNumber = RegistryJson()
            .Replace("\"ordinal\": 4", "\"ordinal\": 4, \"weight\": 1", StringComparison.Ordinal);
        Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassRegistry.Parse(smuggledNumber));
    }

    [Fact]
    public void A_registry_root_must_be_an_object()
    {
        Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassRegistry.Parse("[]"));
    }

    [Fact]
    public void A_registry_with_a_gap_in_its_ordinals_is_refused()
    {
        // Consecutive is a contract, not a documentation sentence: a renumbered ordinal changes
        // what `MAX` over a bundle's members means.
        var json = RegistryJson()
            .Replace("\"ordinal\": 4", "\"ordinal\": 7", StringComparison.Ordinal);

        Assert.Throws<AffixPowerClassRejection>(() => AffixPowerClassRegistry.Parse(json));
    }

    [Fact]
    public void No_power_class_id_equals_a_rarity_rung_id()
    {
        // The two-axes guard, as a test (spec "Boundaries": *never let a power-class id equal a
        // rarity rung id*). Read from the ladder the code itself derives its rung ids from, so a
        // rung added upstream fails here instead of quietly becoming confusable with a class.
        var rungs = new HashSet<string>(RarityLadder.RungIds, StringComparer.Ordinal);
        Assert.NotEmpty(rungs);

        foreach (var id in AffixPowerClassIds.All)
            Assert.DoesNotContain(id, rungs);
    }

    [Fact]
    public void The_registry_declares_no_number_but_a_structural_ordinal()
    {
        // P1 enforced on the FILE, not on a reviewer's memory: the only numbers a power-class
        // registry may hold are the ordinal (which `MAX` orders by) and the two schema versions.
        // A weight, rate, probability, magnitude or target share here would be a balance number
        // living in the wrong place -- the mirror test file asserts the same audit in Python.
        using var doc = JsonDocument.Parse(RegistryJson());
        AssertNoMagnitudeField(doc.RootElement, "ordinal");
    }

    static void AssertNoMagnitudeField(JsonElement node, string allowedKey)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in node.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Number
                        && prop.Name != allowedKey
                        && prop.Name != "schemaVersion"
                        && prop.Name != "registryVersion")
                    {
                        throw new Xunit.Sdk.XunitException(
                            $"power-classes.v1.json holds a number in '{prop.Name}' — identity only");
                    }

                    AssertNoMagnitudeField(prop.Value, allowedKey);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray())
                    AssertNoMagnitudeField(item, allowedKey);
                break;
        }
    }
}
