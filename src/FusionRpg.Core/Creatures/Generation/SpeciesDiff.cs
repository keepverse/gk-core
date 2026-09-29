namespace FusionRpg.Core.Creatures.Generation;

/// <summary>One field where the store-backed and compiled catalogs disagree, for one species.</summary>
public sealed record SpeciesFieldDiff(string SpeciesId, string Field, string Compiled, string StoreBacked);

/// <summary>
/// `catalog-runtime`'s own ⛔ acceptance gate (T4.8 step 4, `spec-catalog-runtime.md` §6): "before
/// deleting `CreatureSpeciesCatalog.Generated.cs`, both sources exist. A test loads the store-backed
/// catalog and the compiled one and diffs them field by field." Pure and Core-only — no I/O, no
/// `RpgStore`, so this can run from any test without a database.
///
/// <para><b>What this proves and what it does not.</b> This module computes the diff and makes it
/// inspectable field-by-field; it does NOT decide which differences are intended. §6's own sentence
/// is explicit that a human accepts them (via `anchor-emit --diff-legacy`, a separate, already-built
/// process) — this type has no "accepted" concept baked in, on purpose, so a future caller cannot
/// mistake "the diff ran" for "a human looked at it."</para>
/// </summary>
public static class SpeciesDiff
{
    /// <summary>Every field difference for every species present in BOTH rosters, ordered by species
    /// id then field name for a stable, diffable report. A species present in only one roster is not
    /// reported here — that is an addition/removal, not a field disagreement (spec §6's own scope:
    /// "for the species present in both").</summary>
    public static IReadOnlyList<SpeciesFieldDiff> Compare(
        IReadOnlyList<CreatureSpeciesDef> compiled, IReadOnlyList<CreatureSpeciesDef> storeBacked)
    {
        var compiledById = compiled.ToDictionary(s => s.SpeciesId, StringComparer.Ordinal);
        var storeById = storeBacked.ToDictionary(s => s.SpeciesId, StringComparer.Ordinal);

        var diffs = new List<SpeciesFieldDiff>();
        foreach (var id in compiledById.Keys.Intersect(storeById.Keys, StringComparer.Ordinal)
                     .OrderBy(id => id, StringComparer.Ordinal))
        {
            var a = compiledById[id];
            var b = storeById[id];

            void Field(string name, string aVal, string bVal)
            {
                if (!string.Equals(aVal, bVal, StringComparison.Ordinal))
                    diffs.Add(new SpeciesFieldDiff(id, name, aVal, bVal));
            }

            Field("name", a.Name, b.Name);
            Field("side", a.Side, b.Side);
            Field("gameTypeId", a.GameTypeId.ToString(), b.GameTypeId.ToString());
            Field("creatureTypeId", a.CreatureTypeId.ToString(), b.CreatureTypeId.ToString());
            Field("elementPrimary", a.ElementPrimary.ToString(), b.ElementPrimary.ToString());
            Field("elementSecondary", a.ElementSecondary?.ToString() ?? "none", b.ElementSecondary?.ToString() ?? "none");
            Field("baseRarity", a.BaseRarity.ToString(), b.BaseRarity.ToString());
            Field("deployMode", a.DeployMode.ToString(), b.DeployMode.ToString());
            Field("acquisition", a.Acquisition.ToString(), b.Acquisition.ToString());
            Field("variants", string.Join(",", a.Variants.OrderBy(v => v, StringComparer.Ordinal)),
                string.Join(",", b.Variants.OrderBy(v => v, StringComparer.Ordinal)));
            Field("traitPool", string.Join(",", a.TraitPool.OrderBy(t => t, StringComparer.Ordinal)),
                string.Join(",", b.TraitPool.OrderBy(t => t, StringComparer.Ordinal)));
        }

        return diffs;
    }

    /// <summary>Species ids present in one roster and not the other — the addition/removal half §6
    /// doesn't ask this module to diff field-by-field, but a reviewer needs to see it too.</summary>
    public static (IReadOnlyList<string> OnlyInCompiled, IReadOnlyList<string> OnlyInStoreBacked) Coverage(
        IReadOnlyList<CreatureSpeciesDef> compiled, IReadOnlyList<CreatureSpeciesDef> storeBacked)
    {
        var compiledIds = compiled.Select(s => s.SpeciesId).ToHashSet(StringComparer.Ordinal);
        var storeIds = storeBacked.Select(s => s.SpeciesId).ToHashSet(StringComparer.Ordinal);
        return (
            compiledIds.Except(storeIds, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList(),
            storeIds.Except(compiledIds, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList());
    }
}
