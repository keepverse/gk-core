namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// Consumer for a STATUS's derived-channel writes — registered as <c>l2b.derived</c> on the Injector
/// Hub (<c>CheatState.ActorHub</c> via <c>StatusDerivedMods.For</c>).
///
/// <para><b>History.</b> Statuses once upserted derived channels only into the PRIMARY session bag
/// (<c>CheatState.Stats.Upsert</c>), so a write to <c>combat.defense.omni</c> composed nothing. This
/// subsystem is the registered reader for live L2b mods; Stance/Exhaustion and payload statuses that
/// mint <see cref="StatusDerivedMod"/> reach Hub through it.</para>
///
/// <para><b>Why a subsystem, not another sink arm.</b> Same argument
/// <see cref="AtomDerivedSubsystem"/> makes: the lawn's derived compose already runs per resolve in
/// <see cref="ActorHub.ResolveDerived"/>; a second delivery path is how private folds grew
/// (actor-hub-ssot.md §6.1).</para>
///
/// <para><b>Why separate from <see cref="AtomDerivedSubsystem"/>.</b>
/// <see cref="ActorHub.Register"/> replaces by <see cref="SubsystemId"/> — a second atom subsystem
/// would evict the first. Statuses and bound atoms have different lifetimes.</para>
///
/// <para><b>Resist feedback</b> (spec-mechanism-wiring.md §12 q1): <c>ResistanceEvaluator</c> reads
/// the defender snapshot, so <c>status.resist.*</c> from a live status affects later applies. The read
/// terminates (dictionary lookup, never nested resolve).</para>
///
/// <para>Stateless between calls. Empty/whitespace <see cref="StatusDerivedMod.SourceId"/> is skipped
/// (GG-49 / actor-hub-ssot §8.1) — same rule as <see cref="AtomDerivedSubsystem"/>.</para>
/// </summary>
public sealed class StatusDerivedSubsystem : IActorStatSubsystem
{
    /// <summary>
    /// The derived-channel mods contributed by the host's currently active statuses. A delegate for
    /// the same reason <see cref="AptitudeSubsystem"/> and <see cref="AtomDerivedSubsystem"/> use one:
    /// this module resolves values into channels, it never owns where instances are stored. Production
    /// passes a projection over the live `StatusRuntime` instances; tests pass a list.
    /// </summary>
    readonly Func<StatContext, IReadOnlyList<StatusDerivedMod>> _modsFor;

    public StatusDerivedSubsystem(Func<StatContext, IReadOnlyList<StatusDerivedMod>>? modsFor = null) =>
        _modsFor = modsFor ?? (_ => Array.Empty<StatusDerivedMod>());

    /// <summary>Namespaced by OWNER, like `rpg.aptitude` and `atom.derived` — and deliberately NOT
    /// `status.derived`, because `status.*` is a live derived-channel family (`status.resist.*`,
    /// `status.immune.*`) so that id reads as a channel claim. `SpecChannelClaimTests` caught it.</summary>
    public string SubsystemId => "l2b.derived";

    /// <summary>After <see cref="AtomDerivedSubsystem"/>'s reserved 350. A status is the shortest-lived
    /// source on this seam, so it folds last among the derived contributors.</summary>
    public int Order => 400;

    public void ContributeDerived(StatContext ctx, ICollection<DerivedModifier> mods)
    {
        var live = _modsFor(ctx);
        if (live is null || live.Count == 0) return;

        foreach (var mod in live)
        {
            if (string.IsNullOrWhiteSpace(mod.Channel)) continue;
            // GG-49 / actor-hub-ssot §8.1: empty SourceId is a defect — skip rather than mint blank.
            if (string.IsNullOrWhiteSpace(mod.SourceId)) continue;
            mods.Add(new DerivedModifier(mod.Channel, mod.Op, mod.Amount, SourceId: mod.SourceId));
        }
    }

    /// <summary>
    /// Maps a status payload's op string to a composer op.
    ///
    /// <para><b><c>more</c> is refused, never coerced.</b> `StatusStatPayload.Ops` allows
    /// `flat | increased | more` because `more` is meaningful on a PRIMARY channel. There is no `More`
    /// on the derived side (effect-atom/definitions.md §14), so a status authoring `more` against a
    /// derived channel is a content error. Coercing it to `Flat` is how a wrong number ships looking
    /// correct — the same reasoning <see cref="AtomDerivedSubsystem.TryParseOp"/> gives.</para>
    /// </summary>
    public static bool TryParseOp(string? op, out DerivedModifierOp parsed)
    {
        switch (op)
        {
            case "flat": parsed = DerivedModifierOp.Flat; return true;
            case "increased": parsed = DerivedModifierOp.Increased; return true;
            default: parsed = default; return false;
        }
    }
}

/// <summary>
/// One derived-channel mod contributed by a live status instance, resolved to what the composer needs.
/// Deliberately not the raw <see cref="Status.StatusStatMod"/>: op parsing and the primary-vs-derived
/// split belong at projection time, not on a per-resolve hot path (perf SSOT — uncached resolves are
/// the measured cost, not scans alone).
///
/// <para><paramref name="SourceId"/> names the status INSTANCE, not the status id, so two coexisting
/// stacks withdraw independently when one expires.</para>
/// </summary>
public readonly record struct StatusDerivedMod(
    string Channel, DerivedModifierOp Op, double Amount, string SourceId);
