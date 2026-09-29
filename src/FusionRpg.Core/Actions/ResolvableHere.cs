using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// combat-ai `resolvable-here` (module 5, CAI1.12, spec-resolvable-here.md §1): what a PLACE can
/// actually execute. Every field is read from the place's own sink and host — never authored in a
/// profile, never copied into tuning.
/// </summary>
public readonly record struct PlaceExecutionProfile(
    RuntimeId Runtime,
    IReadOnlySet<string> ExecutedActions,
    IReadOnlySet<string> RaisedTriggers,
    bool IsPlanner)
{
    /// <summary>The ONE construction entry point: a profile comes from a sink that declares, plus the
    /// host's own raised-trigger set. A null sink is refused loudly — never treated as "executes
    /// everything".</summary>
    public static PlaceExecutionProfile FromSink(
        IDeclaresExecution sink,
        RuntimeId runtime,
        IReadOnlyCollection<string> raisedTriggers,
        bool isPlanner = false)
    {
        if (sink is null) throw new ArgumentNullException(nameof(sink));
        if (raisedTriggers is null) throw new ArgumentNullException(nameof(raisedTriggers));

        return new PlaceExecutionProfile(
            runtime,
            new HashSet<string>(sink.ExecutedActions, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(raisedTriggers, StringComparer.OrdinalIgnoreCase),
            isPlanner);
    }

    public bool Executes(string opcode) => ExecutedActions.Contains(opcode);

    public bool Raises(string trigger) => RaisedTriggers.Contains(trigger);
}

/// <summary>
/// combat-ai `resolvable-here` (module 5, CAI1.12, spec-resolvable-here.md §3): how much of an action's
/// effect surface this place can actually run. <see cref="Full"/> and <see cref="Partial"/> are
/// indistinguishable to the AI today (a Partial action still fires something) — `Partial` exists to be
/// REPORTED (`decision-inspector`, module 10), never to penalise.
/// </summary>
public enum Resolvability
{
    Full = 0,
    Partial = 1,
    Inert = 2,
}

/// <summary>
/// combat-ai `resolvable-here` (module 5, CAI1.12, spec-resolvable-here.md §2): one action's effect
/// surface, computed ONCE per battle from the same action → <c>ContainerId</c> → effectIds walk
/// <c>BindContainers</c> already does. A struct, deliberately: the decision path takes it <c>in</c> and
/// this type must never become one heap object per candidate action per turn.
/// </summary>
public readonly record struct ActionEffectFootprint(
    string ActionId,
    int DeclaredUnits,
    int ResolvableUnits,
    ActionCategory? Category);

/// <summary>
/// combat-ai `resolvable-here` (module 5, CAI1.12, spec-resolvable-here.md §3): the executable-here
/// question, asked at decision time. <c>BindGate</c> asks it at bind time over atom KINDS; this asks it
/// per ACTION over (trigger, opcode) pairs. Two questions, one vocabulary — no second runtime-support
/// matrix, and no authored allowlist anywhere.
/// </summary>
public static class ResolvableHere
{
    /// <summary>Three-way verdict. A zero-unit action is <see cref="Resolvability.Full"/> by
    /// construction, not by exemption: a declarative atom (<c>stat.derived</c>, <c>bullet.modify</c>)
    /// declares no trigger and no opcode because there is nothing for a trigger to fire — it is folded
    /// at resolve time, so an action carrying only declarative atoms is never "inert".</summary>
    public static Resolvability Of(in ActionEffectFootprint f) =>
        f.DeclaredUnits == 0 ? Resolvability.Full
        : f.ResolvableUnits == 0 ? Resolvability.Inert
        : f.ResolvableUnits < f.DeclaredUnits ? Resolvability.Partial
        : Resolvability.Full;

    /// <summary>
    /// The gate. Vetoes ONLY an action whose value is carried entirely by effects that cannot fire
    /// here — <see cref="Resolvability.Inert"/> AND one of the three effect-carried categories.
    /// <para>An <c>Attack</c> or <c>Movement</c> action carries an engine-resolved outcome of its own
    /// (the hit, the move), so an inert rider never removes it from the pool: removing working content
    /// would be a worse failure than the wasted turn this module fixes.</para>
    /// <para>A <c>null</c> category <b>fails open</b>: <c>CompiledAction.Category</c> is null for an
    /// action the corpus has not categorised, and silently removing content the filter cannot classify
    /// is the bug. The <see cref="Resolvability"/> report still surfaces it.</para>
    /// </summary>
    public static bool Veto(in ActionEffectFootprint f) =>
        Of(f) == Resolvability.Inert
        && f.Category is ActionCategory.Support or ActionCategory.Status or ActionCategory.Defense;
}

/// <summary>
/// combat-ai `resolvable-here` (module 5, CAI1.12, spec-resolvable-here.md §2): the per-battle footprint
/// table. Built once at setup from frozen content and read by <c>actionId</c> — never rebuilt per
/// decision, and never keyed by actor (an actor entering a state cannot move its key set).
/// </summary>
public static class EffectFootprintTable
{
    /// <summary>
    /// Walk each distinct action's container once and count its <c>(trigger, opcode)</c> units.
    /// Deduplicated by <c>actionId</c> here, so two actors holding the same action cost ONE walk — the
    /// "computed once per battle" contract.
    /// </summary>
    /// <param name="actions">Every action any actor in this battle holds (duplicates allowed).</param>
    /// <param name="effectIdsFor">The action's container → its compiled effect ids
    /// (<see cref="IContainerEffectResolver.EffectIdsFor"/>).</param>
    /// <param name="defFor">A compiled effect id → its frozen row (<c>EffectBag.Catalog.Get</c>).</param>
    /// <param name="place">The place's own declaration (§1).</param>
    public static IReadOnlyDictionary<string, ActionEffectFootprint> Build(
        IReadOnlyList<CompiledAction> actions,
        Func<string, IReadOnlyList<string>> effectIdsFor,
        Func<string, EffectDef?> defFor,
        in PlaceExecutionProfile place)
    {
        if (actions is null) throw new ArgumentNullException(nameof(actions));
        if (effectIdsFor is null) throw new ArgumentNullException(nameof(effectIdsFor));
        if (defFor is null) throw new ArgumentNullException(nameof(defFor));

        var byAction = new Dictionary<string, ActionEffectFootprint>(StringComparer.Ordinal);
        for (var i = 0; i < actions.Count; i++)
        {
            var action = actions[i];
            if (byAction.ContainsKey(action.ActionId)) continue;
            byAction[action.ActionId] = BuildOne(action, effectIdsFor, defFor, in place);
        }

        return byAction;
    }

    /// <summary>One action's own footprint. Indexed `for`, never `foreach` — the boxed enumerator is
    /// the per-decision allocation this program forbids, and this runs once per action per battle.</summary>
    public static ActionEffectFootprint BuildOne(
        CompiledAction action,
        Func<string, IReadOnlyList<string>> effectIdsFor,
        Func<string, EffectDef?> defFor,
        in PlaceExecutionProfile place)
    {
        var declared = 0;
        var resolvable = 0;

        if (!string.IsNullOrEmpty(action.ContainerId))
        {
            var effectIds = effectIdsFor(action.ContainerId);
            for (var e = 0; e < effectIds.Count; e++)
            {
                var def = defFor(effectIds[e]);
                if (def is null) continue; // a runner-covered container has no bag def: zero units, Full
                if (!def.Enabled) continue;

                for (var t = 0; t < def.Triggers.Count; t++)
                {
                    var trigger = def.Triggers[t];
                    for (var a = 0; a < def.Actions.Count; a++)
                    {
                        declared++;
                        if (place.Raises(trigger) && place.Executes(def.Actions[a].Action)) resolvable++;
                    }
                }
            }
        }

        return new ActionEffectFootprint(action.ActionId, declared, resolvable, action.Category);
    }
}
