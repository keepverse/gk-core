using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>
/// One row of <c>charm_run_hold</c> — the run-start snapshot, in the order it was sealed with.
///
/// <para><b><c>Seq</c> is a determinism input, not a display order.</b> An expedition is "sealed at
/// dispatch by recorded seed" and its resolver is pure over its inputs, so a snapshot that reproduces
/// needs a stable row order — the same reason module 18 put <c>seq</c> on <c>rpg_run_draught</c>
/// rather than folding the manifest into a blob.</para>
/// </summary>
public readonly record struct CharmHold(int Seq, string InstanceId, string ContainerId, string Axis, long ApCost);

/// <summary>
/// One <c>effect_binding</c> row the run start writes. Not written here — Core has no I/O — but shaped
/// here so the DAL and the tests agree about what a charm binding IS.
/// </summary>
/// <param name="OwnerKey">
/// The <b>specimen's</b> id, per D33(a). One binding per deployed actor: the count scales with the
/// squad, which is exactly the cost option B was argued to have and the owner accepted anyway, because
/// the alternative is a scope the resolver cannot express.
/// </param>
/// <param name="InstanceId">
/// The charm instance this binding came from, or <c>null</c> for a resonance tier — a tier is granted
/// BY the snapshot's shape and belongs to no single instance.
/// </param>
public readonly record struct CharmBinding(
    string OwnerKind,
    string OwnerKey,
    string ContainerId,
    string Source,
    int Priority,
    string? InstanceId);

/// <summary>
/// ssot-charms.md §3.8 — the run lifecycle. Attuned set → snapshot → bindings → withdraw by source.
///
/// <para><b>⭐ One snapshot mechanism, two sources.</b> ssot-consumables.md §9 item 10: "whoever builds
/// the run-start snapshot first owns it and the other adopts it." Module 18 built it first
/// (<see cref="FusionRpg.Core.Items.Consumables.DraughtProjection"/>), so this module <b>adopts that
/// lifecycle rather than inventing a second one</b>: snapshot at run start, one binding per held thing
/// with <c>slot = NULL</c> and a negative priority, withdraw at run end BY <c>source</c>. Two
/// independent snapshots over one run is how ordering bugs are born.</para>
///
/// <para><b>⚠ The two differ in exactly two places, and both are rulings rather than drift.</b>
/// <c>source</c> is <c>'charm'</c> here and <c>'draught'</c> there — and the <b>owner scope</b> is
/// <c>unique-actor:{specimenId}</c> here where a draught is <c>player:{id}</c>. That second difference
/// is <b>D33(a)</b>, 2026-09-04: "charms bind at ACTOR scope, not <c>player:</c>". ssot-charms §3.8's
/// prose and ssot-consumables §9 item 10's mirror of it both predate the ruling and still say
/// <c>player:{id}</c>; the ruling wins, because <c>player:</c> is a correctness bug — the stat layer
/// resolves it match-wide and a <c>player:</c>-scoped <c>+atk</c> charm buffs the zombies.</para>
///
/// <para><b>Resonance reuses module 12's evaluator and forks nothing.</b> The tiers come from
/// <see cref="CharmResonance.Consumer"/> driven through <see cref="ThresholdEvaluator"/> — the same
/// machine sets and D3's frame-mix bonus use. A second counting loop here would be the forked copy
/// module 12's central claim forbids.</para>
/// </summary>
public static class CharmRunBinder
{
    /// <summary>
    /// Seal the attuned pouch into an ordered snapshot. Ordering is <b>ordinal by instance id</b>: it
    /// must not depend on the order rows came back from a query, or two replays of one run disagree.
    /// </summary>
    public static IReadOnlyList<CharmHold> Snapshot(IEnumerable<AttunedCharm> attuned)
    {
        if (attuned is null) throw new ArgumentNullException(nameof(attuned));

        return attuned
            .OrderBy(c => c.InstanceId, StringComparer.Ordinal)
            .Select((c, i) => new CharmHold(i, c.InstanceId, c.ContainerId, c.Axis, c.ApCost))
            .ToList();
    }

    /// <summary>The snapshot, back in the shape the gate re-checks at run start (§5.3).</summary>
    public static IReadOnlyList<AttunedCharm> AsAttuned(
        IEnumerable<CharmHold> snapshot, IReadOnlyDictionary<string, CharmDef>? defs = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));

        return snapshot.Select(h => new AttunedCharm(
            h.InstanceId, h.ContainerId, h.Axis, h.ApCost,
            UniqueCarry: defs is not null && defs.TryGetValue(h.ContainerId, out var d) && d.UniqueCarry))
            .ToList();
    }

    /// <summary>
    /// The resonance tiers one snapshot satisfies, per axis, through module 12's evaluator.
    /// Cumulative, so a three-charm axis holds its 2-tier as well as its 3-tier.
    /// </summary>
    public static IReadOnlyList<string> ResonanceTiers(
        IEnumerable<CharmHold> snapshot, IReadOnlyList<CharmResonanceRow> resonanceTable)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (resonanceTable is null) throw new ArgumentNullException(nameof(resonanceTable));

        var held = snapshot.Select(h => new HeldCharm(h.ContainerId, h.Axis)).ToList();
        var wanted = new List<string>();

        foreach (var axis in CharmResonance.AxesOf(resonanceTable))
            wanted.AddRange(ThresholdEvaluator
                .Grant(CharmResonance.Consumer(axis, resonanceTable), held)
                .WantedContainerIds);

        return wanted;
    }

    /// <summary>
    /// Every binding one run start writes: each held charm, plus each satisfied resonance tier, once
    /// per deployed actor.
    ///
    /// <para><b>⛔ It reads the SNAPSHOT and never the live pouch.</b> §3.8: "the RPG works from past
    /// events and contributes a signed delta later; it never reads or guesses current game state", and
    /// an expedition's outcome is sealed at dispatch by recorded seed — a loadout that changes after the
    /// seal makes the seal a lie. A mid-run pouch edit is refused with
    /// <see cref="CharmCarryRefusalReason.CharmInUse"/> at the pouch, never absorbed here.</para>
    /// </summary>
    public static IReadOnlyList<CharmBinding> Bindings(
        IEnumerable<CharmHold> snapshot,
        IReadOnlyList<string> deployedSpecimenIds,
        IReadOnlyList<CharmResonanceRow> resonanceTable,
        CharmAttunementTuning tuning)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (deployedSpecimenIds is null) throw new ArgumentNullException(nameof(deployedSpecimenIds));

        var held = snapshot.OrderBy(h => h.Seq).ToList();
        var tiers = ResonanceTiers(held, resonanceTable);

        var rows = new List<CharmBinding>();
        foreach (var specimenId in deployedSpecimenIds)
        {
            if (string.IsNullOrWhiteSpace(specimenId))
                throw new ArgumentException(
                    "a deployed specimen id is empty; unique-actor: bindings key on it and an empty key " +
                    "is BadOwnerKey at the write", nameof(deployedSpecimenIds));

            foreach (var h in held)
                rows.Add(new CharmBinding(tuning.BindingOwnerKind, specimenId, h.ContainerId,
                    tuning.BindingSource, tuning.BindingPriority, h.InstanceId));

            foreach (var tier in tiers)
                rows.Add(new CharmBinding(tuning.BindingOwnerKind, specimenId, tier,
                    tuning.BindingSource, tuning.BindingPriority, null));
        }

        return rows;
    }

    /// <summary>
    /// The scope gate, delegating to module 12's own refusal rather than restating it — the charm layer
    /// and the resonance layer must not be able to disagree about which scopes are legal.
    /// </summary>
    public static AtomRejection RefuseUnsupportedScope(OwnerScope owner) =>
        CharmResonance.RefuseUnsupportedScope(owner);

    /// <summary>
    /// Withdrawal at run end is <b>by source</b> — one key, one path, and the index already exists on
    /// <c>effect_binding</c>. ⛔ Not a clock: <c>effect_binding</c> carries no expiry, no duration and no
    /// until-tick, so a run-scoped bonus is a lifecycle, never a timer (module 18 established this and
    /// it holds here unchanged).
    /// </summary>
    public static string WithdrawalKey(CharmAttunementTuning tuning) => tuning.BindingSource;
}
