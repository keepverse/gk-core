using FusionRpg.Contracts;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>
/// One `almanac_seed` row's fields, as fed to the builder (spec-creature-corpus-emit.md §2.1).
/// A Core-local input type rather than `FusionRpg.Data`'s `AlmanacSeedDto` — Core cannot depend on
/// Data (DAL boundary), so `gk-forge/tools/CreatureCorpusEmit/Program.cs`, which references both, maps the DTO
/// into this record. Same split `CreatureSpeciesGenerator`'s `CapturedTypeSeed` already uses.
/// </summary>
public sealed record AlmanacSeedRow(
    string Side, int TypeId,
    string? FlavorInfo, string? FlavorIntroduce,
    int? SunCost, double? CooldownSec,
    string CostStatus, // "parsed" | "unparsed" | "absent" — verbatim from the DB column
    long? Hp, long? Attack, long? Armor, long? ArmorMax, bool StatsObserved);

/// <summary>One creature's coverage confidence, carried through unchanged from the capture (§2.3).</summary>
public sealed record CreatureCorpusCoverage(string Cost, string Stats, string Flavor);

/// <summary>
/// Fusion lineage for one creature (§2.4) — raw PvZ type ids, not speciesIds. Downstream readers that
/// want a speciesId join against another entry's `gameTypeId`+`side`; resolving here would force a
/// choice about unresolvable/not-yet-eligible parents that this module has no business making.
///
/// Equality is overridden to compare list CONTENTS, not list identity: the synthesized record
/// equality for a `List&lt;int&gt;` member falls back to reference equality (`List&lt;T&gt;` never
/// overrides `Equals`), which would make two builder runs over identical input compare unequal —
/// silently breaking the byte-identical-across-runs guarantee this whole module exists for (§2.5).
/// </summary>
public sealed record CreatureCorpusLineage(IReadOnlyList<int> Parents, IReadOnlyList<int> Children)
{
    public bool Equals(CreatureCorpusLineage? other) =>
        other is not null && Parents.SequenceEqual(other.Parents) && Children.SequenceEqual(other.Children);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var p in Parents) hash.Add(p);
        hash.Add(-1); // separator so {[1],[2]} and {[1,2],[]} cannot collide
        foreach (var c in Children) hash.Add(c);
        return hash.ToHashCode();
    }
}

/// <summary>
/// One emitted creature-corpus entry (spec §2.2). Deliberately narrow: `creatureTypeId`, element(s),
/// rarity, deploy mode, acquisition, variants and trait pool live ONLY in
/// `CreatureSpeciesCatalog.Generated.cs` — restating them here would create the second source of truth
/// §2.1 forbids. `id`/`name`/`side`/`gameTypeId` are kept because without them the corpus cannot be
/// used, browsed or joined against `recipes` on its own.
/// </summary>
public sealed record CreatureCorpusEntry(
    string Id, string NameKey, string Name, string Side, int GameTypeId,
    string? FlavorInfo, string? FlavorIntroduce,
    long? SunCost, double? CooldownSec,
    long? Hp, long? Attack, long? Armor, long? ArmorMax,
    CreatureCorpusCoverage Coverage, CreatureCorpusLineage Lineage);

/// <summary>
/// Pure `(species, almanac rows, recipe rows) -> creature corpus entries` (spec-creature-corpus-emit.md).
/// No I/O, no clock, no randomness — only `gk-forge/tools/CreatureCorpusEmit/Program.cs` touches the DAL or the
/// filesystem, so this logic is testable without a database (spec §4).
/// </summary>
public static class CreatureCorpusBuilder
{
    public static IReadOnlyList<CreatureCorpusEntry> Build(
        IReadOnlyList<CreatureSpeciesDef> species,
        IReadOnlyList<AlmanacSeedRow> almanac,
        IReadOnlyList<RecipeItem> recipes)
    {
        var almanacByKey = almanac.ToDictionary(a => (a.Side, a.TypeId));

        // Lineage: `recipes` carries no side column, and a raw `type` id is only unique within a
        // side (a plant and a zombie can share the same numeric type). Measured against the live
        // capture (2026-08-31): every recipe parent/result id that resolves to any side resolves to
        // a PLANT id — zero resolve only to a zombie id, and the recorded names (Blover, DoomShroom,
        // IceCherry, ...) are all plant content. So lineage is resolved against plant-side
        // GameTypeIds only; a zombie-side creature's lineage is always empty by construction, not a gap.
        var plantGameTypeIds = species
            .Where(s => s.Side == "plant")
            .Select(s => s.GameTypeId)
            .ToHashSet();

        var parentsOfResult = new Dictionary<int, SortedSet<int>>();
        var childrenOfParent = new Dictionary<int, SortedSet<int>>();
        foreach (var r in recipes)
        {
            if (!plantGameTypeIds.Contains(r.Result)) continue;
            void AddParent(int parentId)
            {
                if (!plantGameTypeIds.Contains(parentId)) return;
                if (!parentsOfResult.TryGetValue(r.Result, out var set))
                    parentsOfResult[r.Result] = set = new SortedSet<int>();
                set.Add(parentId);
                if (!childrenOfParent.TryGetValue(parentId, out var kids))
                    childrenOfParent[parentId] = kids = new SortedSet<int>();
                kids.Add(r.Result);
            }
            AddParent(r.ParentA);
            AddParent(r.ParentB);
        }

        var entries = new List<CreatureCorpusEntry>(species.Count);
        foreach (var s in species.OrderBy(s => s.SpeciesId, StringComparer.Ordinal))
        {
            almanacByKey.TryGetValue((s.Side, s.GameTypeId), out var a);

            var costStatus = a?.CostStatus ?? "absent";
            var statsObserved = a?.StatsObserved ?? false;
            var hasFlavor = !string.IsNullOrWhiteSpace(a?.FlavorInfo)
                             || !string.IsNullOrWhiteSpace(a?.FlavorIntroduce);
            // "present"/"absent", not the spec illustration's three-state "thin": no downstream
            // module (checked: family-extract through creature-themes) reads coverage.flavor at all —
            // each reads flavorInfo/flavorIntroduce directly — and no measured length threshold
            // exists to place a "thin" cut without inventing a magic number on a corpus that isn't
            // a balance surface. If a consumer wants a threshold later, that is a new, cited number.
            var flavorCoverage = hasFlavor ? "present" : "absent";

            // Lineage lookups are keyed by raw GameTypeId only (recipes carry no side column), so a
            // zombie can numerically share an id with an unrelated plant. Only a PLANT-side entry
            // may ever carry lineage — a zombie's is always empty, never looked up, regardless of
            // what a same-numbered plant's recipe participation would otherwise suggest.
            var lineage = s.Side == "plant"
                ? new CreatureCorpusLineage(
                    parentsOfResult.TryGetValue(s.GameTypeId, out var p) ? p.ToList() : Array.Empty<int>(),
                    childrenOfParent.TryGetValue(s.GameTypeId, out var c) ? c.ToList() : Array.Empty<int>())
                : new CreatureCorpusLineage(Array.Empty<int>(), Array.Empty<int>());

            entries.Add(new CreatureCorpusEntry(
                Id: s.SpeciesId,
                NameKey: "creature." + s.SpeciesId,
                Name: s.Name,
                Side: s.Side,
                GameTypeId: s.GameTypeId,
                FlavorInfo: string.IsNullOrEmpty(a?.FlavorInfo) ? null : a!.FlavorInfo,
                FlavorIntroduce: string.IsNullOrEmpty(a?.FlavorIntroduce) ? null : a!.FlavorIntroduce,
                SunCost: a?.SunCost,
                CooldownSec: a?.CooldownSec,
                // Never render an unobserved magnitude as 0 (§2.3) — a null stays null all the way
                // through, distinct from a real 0 the game would report for a free/instant unit.
                Hp: statsObserved ? a?.Hp : null,
                Attack: statsObserved ? a?.Attack : null,
                Armor: statsObserved ? a?.Armor : null,
                ArmorMax: statsObserved ? a?.ArmorMax : null,
                Coverage: new CreatureCorpusCoverage(costStatus, statsObserved ? "observed" : "unobserved", flavorCoverage),
                Lineage: lineage));
        }
        return entries;
    }
}
