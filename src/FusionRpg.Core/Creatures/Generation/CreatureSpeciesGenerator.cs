using System.Text;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>One captured catalog row, as fed to the generator (from `types` via the DAL).</summary>
public sealed record CapturedTypeSeed(string Side, int TypeId, string? TypeName, string? DisplayName, long HpBase);

/// <summary>
/// Deterministic species generation from captured game data (spec-creature-core.md, resolved 2026-08-21):
/// same input rows ⇒ identical roster. Element/rarity/traits derive from typeId hashes and observed
/// HP tiers; the emitted C# is committed so a fresh install needs no game data (gameless-first).
/// </summary>
public static class CreatureSpeciesGenerator
{
    /// <summary>
    /// Generate the species roster. <paramref name="maxSpecies"/> is optional and defaults to
    /// <c>null</c> = <b>no limit</b>: every captured species becomes a creature, so a PVZ update that
    /// adds almanac entries adds creatures with no code change.
    ///
    /// There was a hard cap of 24 here until 2026-08-31, and it bound: with 18 zombie and 66 plant
    /// rows carrying HP data, the pool took all 18 zombies then only 24-18=6 of the 66 plants —
    /// 60 eligible species silently discarded, which is exactly the shipped 18/6 catalog. A ceiling
    /// on the roster means the overlay cannot represent the game it sits on, and every future almanac
    /// entry falls off the end without a word. Pass a limit only for a deliberate, local reason
    /// (a test fixture, a sampling run); never restore a default one.
    ///
    /// The real limit is capture coverage — how many types have observed HP — not a constant.
    /// </summary>
    /// <param name="maxSpecies">null (default) for the whole roster; a positive count to truncate.</param>
    public static List<CreatureSpeciesDef> Generate(IEnumerable<CapturedTypeSeed> captured, int? maxSpecies = null)
    {
        if (maxSpecies is int requested && requested <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSpecies), requested,
                "Pass null for the whole roster; an explicit limit must be positive.");

        // Usable rows: named, alive-capable; zombies first (creatures wear zombie bodies), plants fill.
        var rows = captured
            .Where(c => c.HpBase > 0 && (!string.IsNullOrWhiteSpace(c.TypeName) || !string.IsNullOrWhiteSpace(c.DisplayName)))
            .GroupBy(c => (c.Side, c.TypeId))
            .Select(g => g.First())
            .ToList();

        static IEnumerable<CapturedTypeSeed> ByPower(List<CapturedTypeSeed> src, string side) =>
            src.Where(c => c.Side == side).OrderByDescending(c => c.HpBase).ThenBy(c => c.TypeId);

        // Zombies ranked, then plants ranked. Truncating the concatenation reproduces the old
        // two-step Take exactly when a limit is passed, and takes everything when it is not.
        var ordered = ByPower(rows, "zombie").Concat(ByPower(rows, "plant"));
        var pool = (maxSpecies is int cap ? ordered.Take(cap) : ordered).ToList();

        var species = new List<CreatureSpeciesDef>(pool.Count);
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        for (var rank = 0; rank < pool.Count; rank++)
        {
            var row = pool[rank];
            var rarity = RarityForRank(rank, pool.Count);
            var primary = ElementRoster.Concrete[(int)(Hash(row.TypeId, "element") % (uint)ElementRoster.Concrete.Count)];
            ElementTypeId? secondary = null;
            if (Hash(row.TypeId, "sec") % 3 == 0)
            {
                var s = ElementRoster.Concrete[(int)(Hash(row.TypeId, "sec2") % (uint)ElementRoster.Concrete.Count)];
                if (s != primary) secondary = s;
            }

            species.Add(new CreatureSpeciesDef
            {
                SpeciesId = UniqueId(KebabId(row), usedIds, row.TypeId),
                Name = row.DisplayName ?? row.TypeName ?? $"Creature {row.TypeId}",
                Side = row.Side,
                GameTypeId = row.TypeId,
                // Wide side split: zombie 10000+, plant 60000+ — a zombie typeId can never
                // collide with a plant typeId in the creature id space. One shared function now
                // (species-gear-chain T19), never a restated offset.
                CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFor(row.Side, row.TypeId),
                ElementPrimary = primary,
                ElementSecondary = secondary,
                BaseRarity = rarity,
                DeployMode = rank < 2 ? CreatureDeployMode.HypnoAlly : CreatureDeployMode.PlantAvatar,
                Acquisition = CreatureAcquisition.Summonable,
                Variants = VariantsFor(rarity, row.TypeId),
                TraitPool = TraitsFor(rarity, row.TypeId)
            });
        }

        EnsureElementPresence(species, ElementTypeId.Light);
        EnsureElementPresence(species, ElementTypeId.Dark);
        MarkCaptureExclusives(species);
        return species;
    }

    static CreatureRarity RarityForRank(int rank, int count)
    {
        // Observed-power tiers, all four proportional: ~8% legendary, ~17% epic, ~25% rare, rest
        // common. Legendary was a flat `rank < 2` until 2026-08-31, which stopped scaling once the
        // species cap was removed — at 900 species it still meant exactly two legendaries in the
        // world, while epic and rare grew with the roster.
        //
        // The 1/12 divisor is not a new balance number: it is the ratio the flat 2 already implied
        // on the 24-species roster it was written for (2/24), so the old roster reproduces exactly.
        // The Math.Max floors keep small rosters (test fixtures) from collapsing a tier to zero.
        // Renamed to the ten-rung ladder's own ids (seed-to-concrete T4.1) via the SAME band each
        // old value migrated to (ssot-rarity.md §4.3) — behaviour-preserving, since this generator
        // (and the whole CreatureCatalogGen/Generated.cs path) is superseded and deleted once
        // species-generator/species-import (T4.4-T4.8) land; it exists here only to keep the
        // ASSEMBLY compiling in the meantime, not to be re-run.
        var legendary = Math.Max(2, count / 12);
        var epic = legendary + Math.Max(2, count / 6);
        var rare = epic + Math.Max(3, count / 4);
        if (rank < legendary) return CreatureRarity.Sunwoven;
        if (rank < epic) return CreatureRarity.Heirloom;
        if (rank < rare) return CreatureRarity.Cultivated;
        return CreatureRarity.Chaff;
    }

    static void EnsureElementPresence(List<CreatureSpeciesDef> species, ElementTypeId element)
    {
        if (species.Any(s => s.ElementPrimary == element)) return;
        // Never repurpose a species another ensure-pass just promoted (light then dark would
        // otherwise fight over the same slot), and keep primary != secondary in every path.
        bool Eligible(CreatureSpeciesDef s) =>
            s.ElementSecondary != element &&
            s.ElementPrimary != ElementTypeId.Light &&
            s.ElementPrimary != ElementTypeId.Dark;
        var idx = species.FindIndex(s => s.BaseRarity == CreatureRarity.Chaff && Eligible(s));
        if (idx < 0) idx = species.FindIndex(Eligible);
        if (idx < 0) return;
        species[idx] = species[idx] with { ElementPrimary = element };
    }

    static void MarkCaptureExclusives(List<CreatureSpeciesDef> species)
    {
        // Guardrail: ≤15% capture-only, never legendary. Two deterministic rares become
        // future hunting-ground content (silhouettes until creature-capture ships).
        if (species.Count < 14) return;
        var marked = 0;
        for (var i = 0; i < species.Count && marked < 2; i++)
        {
            if (species[i].BaseRarity != CreatureRarity.Cultivated) continue;
            species[i] = species[i] with { Acquisition = CreatureAcquisition.CaptureOnly };
            marked++;
        }
    }

    static IReadOnlyList<string> VariantsFor(CreatureRarity rarity, int typeId)
    {
        var list = new List<string> { "normal", "shiny" };
        if (CreatureRarityLadder.AtLeast(rarity, CreatureRarity.Heirloom))
        {
            var extras = new[] { "ancient", "mutated", "corrupted", "blessed", "cursed" };
            list.Add(extras[(int)(Hash(typeId, "variant") % (uint)extras.Length)]);
        }

        return list;
    }

    static IReadOnlyList<string> TraitsFor(CreatureRarity rarity, int typeId)
    {
        var combat = new[] { "berserker", "regenerator", "soul-eater", "critical-hunter", "guardian", "swift" };
        var personality = new[] { "loyal", "greedy", "bloodthirsty", "coward", "genius" };
        var pool = new List<string>
        {
            combat[(int)(Hash(typeId, "t1") % (uint)combat.Length)],
            personality[(int)(Hash(typeId, "t2") % (uint)personality.Length)]
        };
        var third = combat[(int)(Hash(typeId, "t3") % (uint)combat.Length)];
        if (!pool.Contains(third)) pool.Add(third);
        if (CreatureRarityLadder.AtLeast(rarity, CreatureRarity.Heirloom))
            pool.Add(Hash(typeId, "essence") % 2 == 0 ? "void-touched" : "chaos-marked");
        if (CreatureRarityLadder.IsTopRung(rarity))
            pool.Add("immortal");
        return pool;
    }

    static string KebabId(CapturedTypeSeed row)
    {
        var source = !string.IsNullOrWhiteSpace(row.TypeName) ? row.TypeName! : $"creature-{row.TypeId}";
        var sb = new StringBuilder(source.Length + 8);
        var lastDash = true;
        foreach (var ch in source)
        {
            if (ch is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'))
            {
                sb.Append(char.ToLowerInvariant(ch));
                lastDash = false;
            }
            else if (!lastDash)
            {
                sb.Append('-');
                lastDash = true;
            }
        }

        var id = sb.ToString().Trim('-');
        return id.Length == 0 ? $"creature-{row.TypeId}" : id;
    }

    static string UniqueId(string baseId, HashSet<string> used, int typeId)
    {
        var id = used.Add(baseId) ? baseId : $"{baseId}-{typeId}";
        if (id != baseId) used.Add(id);
        return id;
    }

    /// <summary>FNV-1a over (typeId, salt) — the deterministic randomness source; never wall clock, never Random.</summary>
    static uint Hash(int typeId, string salt)
    {
        unchecked
        {
            var h = 2166136261u;
            foreach (var ch in salt)
            {
                h ^= ch;
                h *= 16777619u;
            }

            h ^= (uint)typeId;
            h *= 16777619u;
            return h;
        }
    }

    /// <summary>Emit the checked-in Generated.cs source for a roster.</summary>
    public static string EmitCSharp(IReadOnlyList<CreatureSpeciesDef> species)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated>");
        sb.AppendLine("// Creature species roster generated from captured game data (spec-creature-core.md).");
        sb.AppendLine("// Regenerate: dotnet run --project tools/CreatureCatalogGen -- <server data dir>");
        sb.AppendLine("// Do not hand-edit — rebalance via the generator, then re-emit.");
        sb.AppendLine("// </auto-generated>");
        sb.AppendLine("using FusionRpg.Core.Stats.Derived;");
        sb.AppendLine();
        sb.AppendLine("namespace FusionRpg.Core.Creatures;");
        sb.AppendLine();
        sb.AppendLine("public static partial class CreatureSpeciesCatalog");
        sb.AppendLine("{");
        sb.AppendLine("    static readonly IReadOnlyList<CreatureSpeciesDef> GeneratedSpecies = new CreatureSpeciesDef[]");
        sb.AppendLine("    {");
        foreach (var s in species)
        {
            sb.Append("        new() { SpeciesId = ").Append(Quote(s.SpeciesId));
            sb.Append(", Name = ").Append(Quote(s.Name));
            sb.Append(", Side = ").Append(Quote(s.Side));
            sb.Append(", GameTypeId = ").Append(s.GameTypeId);
            sb.Append(", CreatureTypeId = ").Append(s.CreatureTypeId);
            sb.Append(", ElementPrimary = ElementTypeId.").Append(s.ElementPrimary);
            sb.Append(", ElementSecondary = ").Append(s.ElementSecondary is { } sec ? $"ElementTypeId.{sec}" : "null");
            sb.Append(", BaseRarity = CreatureRarity.").Append(s.BaseRarity);
            sb.Append(", DeployMode = CreatureDeployMode.").Append(s.DeployMode);
            sb.Append(", Acquisition = CreatureAcquisition.").Append(s.Acquisition);
            sb.Append(", Variants = new[] { ").Append(string.Join(", ", s.Variants.Select(Quote))).Append(" }");
            sb.Append(", TraitPool = new[] { ").Append(string.Join(", ", s.TraitPool.Select(Quote))).Append(" }");
            sb.AppendLine(" },");
        }

        sb.AppendLine("    };");
        sb.AppendLine("}");
        return sb.ToString();
    }

    static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
}
