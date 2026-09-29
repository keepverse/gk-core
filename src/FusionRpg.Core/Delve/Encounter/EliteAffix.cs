using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Encounter;

/// <summary>
/// `encounter-generator` D2.6 (spec-encounter-generator.md §6) — an elite (or a boss phase) is one
/// slot with an affix roll through <see cref="Instantiator.TryInstantiate"/>, never a private roll of
/// its own. `ContainerKind.Enemy` is this task's own seventh kind (`ContainerRow.cs`,
/// `ContainerValidator.cs`).
///
/// <para><b>What this module does NOT build, and why — the library gap, named as the external
/// dependency it is (§6's own words).</b> `gk-data/packs/fusion/data/seed/effects/affixes/all.json` holds exactly two
/// entries today (read in full, 2026-09-06): both plain `{id, name, class, refs}` — no `tags` field,
/// and <see cref="AffixRow"/> itself (`ContainerRow.cs:93`) carries no tag concept at all. "Pool =
/// every affix-library row tagged for enemies" (§6) is therefore not this method's own filter to run —
/// there is no tag to filter BY yet. <see cref="Apply"/> takes the already-filtered
/// <c>pool</c> as a plain parameter (the same "read model owned elsewhere" shape
/// <see cref="Encounter.Build"/>'s own anchor and corpus already are), and correctly degrades to
/// "no affix" for exactly the reason the real pool is empty today: nothing is tagged yet, so a real
/// call against the real corpus always takes the degradation path, which is proven directly below
/// rather than assumed.</para>
///
/// <para><b><c>exclusiveTags</c> needs no mechanism here either</b> (§6, verbatim) — each tag becomes
/// the <see cref="ContainerPoolRow.Group"/> of the pool rows carrying it, and the ALREADY-BUILT
/// one-per-group rule (<c>ContainerValidator.cs:33-38</c>, exercised on every
/// <see cref="Instantiator.TryInstantiate"/> call) refuses the pair. Nothing new to write.</para>
///
/// <para><b>Genuinely deferred: persisting the rolled instance for a later
/// <c>IContainerEffectResolver</c> to answer from.</b> This module is pure (§9: no store) and returns
/// the rolled <see cref="InstanceRow"/> directly; making a LATER battle bind find that SAME instance
/// (rather than re-rolling, which would silently break "reproduces over `(container, revision,
/// rollSeed, Θ_room)`" the moment anything else in the pipeline is not bit-for-bit identical) needs
/// somewhere to persist it — outside this module's own "no store" boundary, and no delve-specific
/// <c>IContainerEffectResolver</c> implementation exists anywhere in `src/` yet either. Named here
/// rather than silently assumed solved.</para>
/// </summary>
public static class EliteAffix
{
    /// <summary>
    /// Rolls one `enemy.*` container for <paramref name="containerIdSuffix"/> (the caller's own
    /// `{encounterId}-{elite|boss-p{n}}`, per §6's id grammar) and, on success, appends its
    /// `ContainerId` to <paramref name="actor"/>'s <see cref="BattleActorSetup.GrantedContainerIds"/>.
    /// On an empty pool or a refused roll, <paramref name="actor"/> is returned untouched and a
    /// warning names why — <b>never a fake affix, never a flat stat bump</b> (§6).
    /// </summary>
    public static (BattleActorSetup Actor, string? Warning) Apply(
        BattleActorSetup actor, string containerIdSuffix, string? rarity, int? minTier, int? maxTier,
        int prefixRolls, int suffixRolls, IReadOnlyList<ContainerPoolRow> pool,
        Func<string, AtomRow?> lookupAtom, Func<string, AffixRow?> lookupAffix,
        long rollSeed, int thetaRoom, PowerTuning tuning)
    {
        if (actor is null) throw new ArgumentNullException(nameof(actor));
        if (string.IsNullOrEmpty(containerIdSuffix)) throw new ArgumentException("containerIdSuffix is required.", nameof(containerIdSuffix));
        if (pool is null) throw new ArgumentNullException(nameof(pool));
        if (lookupAtom is null) throw new ArgumentNullException(nameof(lookupAtom));
        if (lookupAffix is null) throw new ArgumentNullException(nameof(lookupAffix));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var containerId = $"enemy.{containerIdSuffix}";

        if (pool.Count == 0)
            return (actor, $"'{containerId}': affix pool is empty — degraded to no affix, never a fake one");

        var container = new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.Enemy,
            Rarity = rarity,
            MinTier = minTier,
            MaxTier = maxTier,
            PrefixRolls = prefixRolls,
            SuffixRolls = suffixRolls,
            Pool = pool,
        };

        var rejection = Instantiator.TryInstantiate(container, lookupAtom, lookupAffix, rollSeed, thetaRoom, tuning, out var instance);
        if (!rejection.IsOk)
            return (actor, $"'{containerId}' refused ({rejection}) — degraded to no affix, never a fake one");

        var granted = (actor.GrantedContainerIds ?? Array.Empty<string>()).Append(containerId).ToList();
        return (actor with { GrantedContainerIds = granted }, null);
    }
}
