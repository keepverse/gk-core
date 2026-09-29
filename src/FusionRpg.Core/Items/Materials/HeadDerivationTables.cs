namespace FusionRpg.Core.Items.Materials;

/// <summary>The minimal base-type shape EITHER head table needs — id, material class, tags. The
/// corpus row carries more (role, frame, socketMax); this type carries only what the derivation
/// reads, not a second copy of the row.</summary>
public sealed record HeadDerivationEntry(string Id, string Class, IReadOnlyList<string> Tags);

public sealed class HeadDerivationRejection : Exception
{
    public HeadDerivationRejection(string message) : base(message) { }
}

/// <summary>Crafting potential's derivation (species-gear-chain T10, spec-craft-risk-ladder.md
/// §Design 2): DERIVED, with an authored per-base-type override. `max(class, rarity) =
/// baseByClass[class] × rarityMultiplierMilli[rung] / 1000` — `checked`, widen-first (`(long)base *
/// milli`, never the cast-after-multiply that has already overflowed), divide-last, `long`
/// throughout; overflow throws, never wraps. Unknown class or rung ids refuse naming the base
/// type, never default.
///
/// <para>EXPLICIT OR ABSENT, never a sentinel (tunables-ssot.md T5): absent means DERIVE; a present
/// override wins; a base type declared in `overrideExpected` but carrying none is a LOAD REJECTION
/// naming it — a silent fallback would make the derived and authored paths indistinguishable.</para>
/// </summary>
public static class PotentialTable
{
    public static long DeriveMax(
        HeadDerivationEntry entry, string rungId, DeploymentHierarchyTuning tuning)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (tuning.PotentialOverrides.TryGetValue(entry.Id, out var authored))
            return authored;

        if (tuning.PotentialOverrideExpected.Contains(entry.Id, StringComparer.Ordinal))
            throw new HeadDerivationRejection(
                $"craft potential: base type '{entry.Id}' is declared to carry an authored override but " +
                "none is present — an expected-but-missing override is a rejection, never a derive");

        if (!tuning.PotentialBaseByClass.TryGetValue(entry.Class, out var baseValue))
            throw new HeadDerivationRejection(
                $"craft potential: base type '{entry.Id}' has unknown class '{entry.Class}'");
        if (!tuning.PotentialRarityMultiplierMilli.TryGetValue(rungId, out var multiplier))
            throw new HeadDerivationRejection(
                $"craft potential: base type '{entry.Id}' names unknown rung '{rungId}'");

        checked
        {
            return (baseValue * multiplier) / 1000;
        }
    }
}

/// <summary>Durability's derivation (`deployment-hierarchy` module 7 §2, pulled forward as
/// species-gear-chain T11): `max(class, rarity) = baseByClass[class] × rarityMultiplierMilli[rung]
/// / 1000`, same `checked`, widen-first, divide-last `long` shape. PURELY derived — no override:
/// `:408`'s Never stands for instance-level authoring, and the potential override above is catalog
/// level (see the filed reconciliation in `deployment-hierarchy-map.md`), so the asymmetry is
/// deliberate.</summary>
public static class DurabilityTable
{
    public static long DeriveMax(
        HeadDerivationEntry entry, string rungId, DeploymentHierarchyTuning tuning)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        if (!tuning.DurabilityBaseByClass.TryGetValue(entry.Class, out var baseValue))
            throw new HeadDerivationRejection(
                $"durability: base type '{entry.Id}' has unknown class '{entry.Class}'");
        if (!tuning.DurabilityRarityMultiplierMilli.TryGetValue(rungId, out var multiplier))
            throw new HeadDerivationRejection(
                $"durability: base type '{entry.Id}' names unknown rung '{rungId}'");

        checked
        {
            return (baseValue * multiplier) / 1000;
        }
    }
}
