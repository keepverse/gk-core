using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Battle;

/// <summary>
/// Where a trait's static channel mods come from (spec-trait-migration.md, E12).
///
/// <para><b>One trait migrates, not seven.</b> The map said "the 7 funnel-routed traits become
/// containers of atoms", and that was wrong twice over: there are 14 traits, and <c>FunnelRouted</c>
/// classifies which traits the contracts module layers obedience onto — not which are
/// atom-expressible. Checked against the 12 kinds, only <c>critical-hunter</c> survives, because
/// <c>stat.derived</c> ChannelMods merge at <b>compose</b> time, a path battle already runs.
/// <c>regenerator</c> and <c>soul-eater</c> need event dispatch battle does not have; the rest need
/// kinds that would break the 12-kind ceiling to serve four content rows.</para>
///
/// <para><b>Both paths stay runnable.</b> A trait with bound atoms reads them; every other trait
/// reads <see cref="TraitBattleCatalog"/> exactly as before. That is what makes the predicted delta
/// measurable rather than asserted — the parity test composes the same squad down both roads.</para>
/// </summary>
public sealed class TraitAtomSource
{
    readonly Dictionary<string, List<BattleChannelMod>> _byTrait;

    TraitAtomSource(Dictionary<string, List<BattleChannelMod>> byTrait) => _byTrait = byTrait;

    /// <summary>Nothing migrated — every trait reads the catalog. The pre-E12 behaviour.</summary>
    public static readonly TraitAtomSource CatalogOnly = new(new(StringComparer.Ordinal));

    /// <summary>
    /// The migrated traits, as Core holds them.
    ///
    /// <para>Mirrors <c>gk-data/packs/fusion/data/seed/{atoms,containers}/trait-critical-hunter.json</c>, which is the
    /// authored source; a test proves the two agree, so they cannot drift. Core does not read the
    /// files itself — the same rule that keeps a runtime content loader out of this program — and the
    /// same shape <see cref="Combat.Element.ElementTable.Shipped"/> uses for the roster.</para>
    ///
    /// <para><b>Exactly one entry, and that is the finding.</b> Thirteen traits stay on the catalog
    /// because the atom vocabulary cannot express them, not because nobody got to them.</para>
    /// </summary>
    public static TraitAtomSource Shipped() => new(new Dictionary<string, List<BattleChannelMod>>(
        StringComparer.Ordinal)
    {
        // +150 over the −250 parity baseline → σ(−1.0) ≈ 26.9% crit, against a 7.6% base.
        ["critical-hunter"] = new()
        {
            new BattleChannelMod(Stats.Derived.DerivedStatChannels.CombatCritRateOmni, 150),
        },
    });

    /// <summary>
    /// Build from bound containers. A container id of <c>trait.&lt;id&gt;</c> supplies that trait;
    /// only <c>stat.derived</c> atoms contribute, because that is the one kind whose consumer battle
    /// has.
    /// </summary>
    public static TraitAtomSource FromContainers(
        IReadOnlyList<ContainerRow> containers, Func<string, AtomRow?> atomOf)
    {
        var byTrait = new Dictionary<string, List<BattleChannelMod>>(StringComparer.Ordinal);

        foreach (var container in containers)
        {
            if (container.Kind != ContainerKind.Trait) continue;
            var traitId = TraitIdOf(container.ContainerId);
            if (traitId is null) continue;

            var mods = new List<BattleChannelMod>();
            foreach (var reference in container.Atoms.OrderBy(a => a.Seq))
            {
                var atom = atomOf(reference.AtomId);
                if (atom is null) continue;

                // Only stat.derived. A trait container carrying anything else is content whose
                // consumer battle does not have — accepting it would be the silent no-op that
                // quarantined this kind in the first place.
                if (!string.Equals(atom.KindId, "stat.derived", StringComparison.Ordinal)) continue;

                var pars = Effects.Atoms.Power.CostFunction.Read(atom.ParamsJson);
                if (!pars.TryGetValue("channel", out var chEl)
                    || chEl.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                if (!pars.TryGetValue("amount", out var amtEl)
                    || !amtEl.TryGetInt32(out var amount)) continue;

                mods.Add(new BattleChannelMod(chEl.GetString()!, amount));
            }

            if (mods.Count > 0) byTrait[traitId] = mods;
        }

        return new TraitAtomSource(byTrait);
    }

    public bool IsMigrated(string traitId) => _byTrait.ContainsKey(traitId);

    /// <summary>
    /// The trait's channel mods — from its atoms when it has been migrated, from the catalog
    /// otherwise. The fallback is not a convenience: thirteen traits are deliberately unmigrated,
    /// and they must keep behaving identically.
    /// </summary>
    public IReadOnlyList<BattleChannelMod> ModsFor(string traitId) =>
        _byTrait.TryGetValue(traitId, out var mods)
            ? mods
            : TraitBattleCatalog.Get(traitId).ChannelMods;

    /// <summary>Container ids follow definitions §1: <c>trait.&lt;kebab-id&gt;</c>.</summary>
    static string? TraitIdOf(string containerId) =>
        containerId.StartsWith("trait.", StringComparison.Ordinal)
            ? containerId["trait.".Length..]
            : null;
}
