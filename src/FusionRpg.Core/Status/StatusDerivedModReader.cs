using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Status;

/// <summary>
/// Projects a host's live status instances into the <see cref="StatusDerivedMod"/> list
/// <see cref="StatusDerivedSubsystem"/> composes — the Unity-free half of the injector's
/// `StatusDerivedMods.For` adapter (mechanism-wiring G1's injector half, closing the gap named in
/// spec-mechanism-wiring.md §4.1: a status's derived-channel write reaches
/// <see cref="StatusDerivedSubsystem"/> only through a real production caller).
///
/// <para><b>Same split as <see cref="GrantedDerivedAtomReader"/> (aura-skill-todo.md Phase 5 / TC3).</b>
/// Everything here is Unity-free by construction — <see cref="StatusRuntime"/>, <see cref="StatusInstance"/>
/// and <see cref="StatContext"/> are all Core types — so it is testable by CI. What the injector keeps is
/// exactly the host-specific fact: reaching the live <c>EffectRuntime.Status</c> static.</para>
///
/// <para><b>Only DERIVED channels project.</b> <see cref="StatusStatPayload.IsKnownChannel"/> accepts
/// both the 23 PRIMARY channels — already composed by <c>StatSystem.Resolve</c> via
/// <see cref="StatusStatPayload.ToModifiers"/> (<c>EffectRuntime.cs:81</c>) — and derived ones
/// (<c>combat.*</c>, <c>status.power.*</c>/<c>status.resist.*</c>). Re-projecting a primary-channel mod
/// here would double it: applied once as a primary modifier and again as a derived one. Which channels
/// count as derived is decided by <see cref="StatusStatPayload.IsDerivedChannel"/> — mechanism-wiring
/// E2 extracted that predicate to ONE place, read by both this runtime-side filter and the parser's own
/// at-parse `more` refusal, so the two can never independently drift on what "derived" means.</para>
///
/// <para><b><c>SourceId</c> names the status INSTANCE</b>, via <see cref="StatusStatPayload.SourceIdOf"/> —
/// the identical id the primary path withdraws by (<c>EffectRuntime.cs</c>'s
/// <c>WithdrawSource("status", StatusStatPayload.SourceIdOf(inst))</c>) — so two coexisting stacks of the
/// same status (`ember`'s shipped Coexist stacking) withdraw independently.</para>
///
/// <para><c>more</c> is refused, never coerced, via <see cref="StatusDerivedSubsystem.TryParseOp"/> —
/// the same op parser the subsystem itself uses, so the reader and the composer can never disagree about
/// what a status op means.</para>
/// </summary>
public static class StatusDerivedModReader
{
    /// <summary>
    /// Every derived-channel mod the host named by <paramref name="ctx"/>'s <c>EntityKey</c> currently
    /// contributes. Never null; never throws — a runtime that is not up yet, or a context with no entity
    /// key (a type-level resolve), is a normal state, not an error, mirroring
    /// <see cref="GrantedDerivedAtomReader"/>'s own null/empty handling.
    /// </summary>
    public static IReadOnlyList<StatusDerivedMod> Read(StatusRuntime? runtime, StatContext? ctx)
    {
        if (runtime is null || ctx is null || string.IsNullOrWhiteSpace(ctx.EntityKey))
            return Array.Empty<StatusDerivedMod>();

        IReadOnlyList<StatusInstance> instances;
        try { instances = runtime.ForHost(ctx.EntityKey); }
        catch { return Array.Empty<StatusDerivedMod>(); }

        return Read(instances);
    }

    /// <summary>
    /// The pure projection, split out so a test (or a future caller with its own instance source) can
    /// pass a hand-built list without standing up a whole <see cref="StatusRuntime"/>.
    /// </summary>
    public static IReadOnlyList<StatusDerivedMod> Read(IReadOnlyList<StatusInstance>? instances)
    {
        if (instances is null || instances.Count == 0) return Array.Empty<StatusDerivedMod>();

        List<StatusDerivedMod>? found = null;

        foreach (var instance in instances)
        {
            if (instance is null || instance.StatMods.Count == 0) continue;
            var sourceId = StatusStatPayload.SourceIdOf(instance);

            foreach (var mod in instance.StatMods)
            {
                // The SAME predicate the parser uses to decide "is this a derived channel" — extracted
                // to StatusStatPayload.IsDerivedChannel (mechanism-wiring E2) so this runtime-side
                // filter and the parser's at-parse `more` refusal can never independently drift.
                if (!StatusStatPayload.IsDerivedChannel(mod.ChannelId)) continue;

                // There is no `More` on the derived side (definitions.md §14). Skipping rather than
                // coercing to Flat is how a wrong number is refused instead of shipping looking correct
                // — the same reasoning StatusDerivedSubsystem.TryParseOp itself gives. `more` against a
                // derived channel is already refused at parse (StatusStatPayload.TryParse), so a live
                // instance carrying one here would mean parse-time content validation was bypassed;
                // this stays as defence in depth rather than an assumption that parse always ran.
                if (!StatusDerivedSubsystem.TryParseOp(mod.Op, out var op)) continue;

                (found ??= new List<StatusDerivedMod>()).Add(
                    new StatusDerivedMod(mod.ChannelId, op, mod.Value, sourceId));
            }
        }

        return (IReadOnlyList<StatusDerivedMod>?)found ?? Array.Empty<StatusDerivedMod>();
    }
}
