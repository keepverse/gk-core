using FusionRpg.Core.Delve.Difficulty;
using FusionRpg.Core.Dungeon.Tuning;
using FusionRpg.Core.Power;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// D4.17 row 10's own production bridge (party-dungeon-todo.md, 2026-09-23) — wires the real
/// `RungOffer.For` (difficulty-ladder, already shipped) into
/// <see cref="DomainPreflightInputs.OfferedRungCountFor"/>.
///
/// <para>Row 10's own 2026-09-07 investigation left this open on `ParentWorldTerms` having zero
/// production producers anywhere. That gap has since closed elsewhere:
/// `ParentWorldTermsSource.For` (narrative hosts, NR2.18) is now the ONE owned construction site,
/// returning absence terms `(0, 0, 0)` for a null world with documented "zero is absence, not
/// corruption" semantics — so this bridge takes its `world` as a plain caller-supplied value and
/// the import-time caller passes that provider's absence reading, never an invented literal.</para>
///
/// <para>The count is, in fact, independent of which world terms the caller passes: the two inputs
/// that decide offered-vs-refused — the band floor (`RoomThetaComposer` throwing
/// `RungNotOffered`, `RungOffer.cs:56-62`) and the oath unlocks
/// (`OathUnlock.IsRungOffered`, `RungOffer.cs:47`) — read neither `WorldTier`, `ZombossLevel`
/// nor `RealmsAdvanced` (those enter `ContentContext` for Θ only, `RoomTheta.cs:66`). The owned
/// provider is still the right source — it keeps this bridge free of a second construction site
/// (its own scan test refuses one) — but a wrong-world worry cannot move this verdict, proven by
/// test with two different worlds giving identical counts.</para>
///
/// <para>Counts OFFERED rungs only (`r.Offered`), matching spec-domain-catalog.md §2 row 10's own
/// normative line ("offers ≥ 1 rung", `domain.no-rung-offered`) — §6's looser pseudocode counts
/// `.Rungs.Count`, which is never zero since `For` always returns the full ten-row ladder with
/// per-row refusal reasons. At import-time clears (`PlayerClears.None`, the spec's own `noClears`
/// citation verbatim) the tail contributes nothing anyway (step 1 needs a rung-10 clear,
/// `OathUnlock.IsTailStepOffered`), so rungs-only and rungs-plus-tail agree here regardless.</para>
///
/// <para>An unknown `DangerBand` throws `KeyNotFoundException` from the ordinals lookup rather
/// than mapping silently — unreachable in a real chain (row 1, `DomainCatalog.Load` over the
/// same mapping, refuses first), the same loud-refusal posture `DomainAnchorBuilder` already
/// uses for an unreal room id.</para>
/// </summary>
public static class DomainRungOffer
{
    public static Func<DomainRow, int> Build(
        PowerTuning power, DungeonTuning dungeon,
        IReadOnlyDictionary<string, int> dangerBandOrdinals, ParentWorldTerms world)
    {
        if (power is null) throw new ArgumentNullException(nameof(power));
        if (dungeon is null) throw new ArgumentNullException(nameof(dungeon));
        if (dangerBandOrdinals is null) throw new ArgumentNullException(nameof(dangerBandOrdinals));
        if (world is null) throw new ArgumentNullException(nameof(world));

        return domain =>
        {
            if (domain is null) throw new ArgumentNullException(nameof(domain));
            var theta = new DomainThetaInputs(
                EntranceBand: dangerBandOrdinals[domain.DangerBand],
                IsOnceEntry: string.Equals(domain.Entry, "once", StringComparison.Ordinal),
                PermadeathFromRungOverride: domain.PermadeathFromRung);
            return RungOffer.For(power, dungeon, theta, world, PlayerClears.None)
                .Rungs.Count(r => r.Offered);
        };
    }
}
