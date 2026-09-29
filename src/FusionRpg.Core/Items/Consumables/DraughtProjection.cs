using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Items.Consumables;

/// <summary>
/// One resolved draught line: a derived channel and the signed amount it contributes for the run.
///
/// <para>⏸ <b>The amount arrives from the caller, and that is the seam, not a shortcut.</b> The 60-row
/// corpus holds <b>seeds</b> — a family and a <c>powerBand</c>, never a magnitude (seed-contract.md §3)
/// — so rolling a seed into a concrete container with real atom rows is the runtime generator's job
/// under the binding seed-to-concrete rule. This type takes what that generator produces; module 11
/// used the same injected-delegate shape for step 9's mint.</para>
/// </summary>
/// <param name="ContainerId">For the audit line and for the manifest's own ordering.</param>
/// <param name="Channel">A derived-stat channel id, e.g. <c>combat.power.fire</c>.</param>
/// <param name="Amount">
/// <b><c>long</c>, because it is a magnitude</b> (AGENTS.md), and signed only so the type can carry a
/// drawback later; <see cref="DraughtProjection.ToDerivedModifiers"/> refuses a non-positive one today
/// via its shared <c>Validate</c> — see there.
/// </param>
public readonly record struct DraughtMod(string ContainerId, string Channel, long Amount);

/// <summary>
/// ssot-consumables.md §5.4 — how a draught reaches an actor, and why it is a <b>projection</b> rather
/// than a binding in v1.
///
/// <para>The scopes are seven and <b>there is no <c>actor:{instanceId}</c></b> (definitions §6), so a
/// per-specimen draught cannot be a binding. It does not need to be: it reaches the actor as an
/// attributed Hub contribution instead (<see cref="ToDerivedModifiers"/> via
/// <see cref="Subsystems.DraughtSubsystem"/>), the same road <c>ExpeditionResolver.ApplyInjuries</c>
/// already drives for injuries — setting <c>BattleHubInputs.Injuries</c> on each victim before the
/// battles resolve, since battle-hub-fuse (T6) retired the pre-folded <c>ChannelMods</c> form both
/// used to take.</para>
///
/// <para><b>A draught is the same transform with the opposite sign. That is the whole v1 runtime.</b></para>
///
/// <para>⭐ v1 is <b>per-squad</b>, which is §10.4's own answer: every member receives every mod. Making
/// it per-specimen is expressible on this same road (the mods are already per-actor) and would turn the
/// manifest into a targeting decision — recorded as the owner's, not decided here.</para>
/// </summary>
public static class DraughtProjection
{
    /// <summary>
    /// channelmods-hub T2 — the same validated manifest as attributed <c>Flat</c> derived
    /// contributions (one per draught, source <c>grant:draught:{ContainerId}</c> through
    /// <see cref="ContributionSourceIds.Grant"/> — GG-49 has no draught family, and the plan's
    /// Ask-first default reuses families already in use).
    /// <see cref="Subsystems.DraughtSubsystem"/> contributes these on the Hub path — battle-hub-fuse
    /// T6 deleted the old <c>BattleChannelMod</c> adapter this replaced (zero production callers).
    /// </summary>
    public static IReadOnlyList<DerivedModifier> ToDerivedModifiers(IReadOnlyList<DraughtMod> draughts)
    {
        var list = Validate(draughts);
        return list.Select(d => new DerivedModifier(
            d.Channel, DerivedModifierOp.Flat, d.Amount,
            SourceId: ContributionSourceIds.Grant($"draught:{d.ContainerId}"))).ToList();
    }

    static IReadOnlyList<DraughtMod> Validate(IReadOnlyList<DraughtMod> draughts)
    {
        draughts ??= Array.Empty<DraughtMod>();
        foreach (var d in draughts)
        {
            if (string.IsNullOrWhiteSpace(d.Channel))
                throw new ArgumentException(
                    $"draught '{d.ContainerId}' names no channel", nameof(draughts));
            if (d.Amount <= 0)
                throw new ArgumentOutOfRangeException(nameof(draughts),
                    $"draught '{d.ContainerId}' contributes {d.Amount} to '{d.Channel}'; a draught is " +
                    "ApplyInjuries with the OPPOSITE sign (§5.4), so a non-positive amount is a content " +
                    "defect and throws rather than being clamped to nothing");
        }
        return draughts;
    }

    /// <summary>
    /// The run-start binding shape §4.3 / §9 item 10 fixes for the player-wide half — <b>one snapshot
    /// mechanism, two sources.</b> Charms bind at <c>player:{id}</c> with <c>source = 'charm'</c>;
    /// draughts do the same with <c>source = 'draught'</c>, the same <c>slot = NULL</c> and the same
    /// priority. "Whoever builds the run-start snapshot first owns it and the other adopts it" — module
    /// 22 <c>charm-carry</c> is unbuilt, so this module owns it and the charm side adopts this shape.
    /// </summary>
    public const string BindingSource = "draught";

    /// <summary>The owner scope a run-scoped draught binds at. There is no actor scope (§5.4).</summary>
    public const string BindingOwnerKind = "player";

    /// <summary>
    /// Withdrawal is <b>by source</b>, at run end — the index for which already exists on
    /// <c>effect_binding</c>. ⛔ It is NOT a clock: <c>effect_binding</c> carries no expiry, no
    /// duration and no until-tick (verified in the shipped DDL), so a timed buff must be a status and a
    /// run-scoped buff is a lifecycle. v1 uses the second, because it needs nothing new.
    /// </summary>
    public static string WithdrawalKey => BindingSource;
}
