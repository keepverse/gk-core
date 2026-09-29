using FusionRpg.Core.World.Intel;
using FusionRpg.Core.World.Turn;

namespace FusionRpg.Core.World.Ai;

/// <summary>
/// One order, plus why it was given.
///
/// The reason is deliberately *not* a field on <see cref="WorldCommand"/>: that record is the replay
/// unit — a save is `(seed, template, command log)` — and an audit string has no business inside it.
/// It travels beside the command as far as the store and is written to its own column.
/// </summary>
public readonly record struct PolicyOrder(WorldCommand Command, string Reason);

/// <summary>
/// A commander that is not a person (spec-ai-commander.md §The decision layer).
///
/// The whole design rests on where this runs: **outside <c>TurnEngine.Step</c>, before the barrier.**
/// A policy files commands like any other commander, so its decisions become data in the command
/// log. Replay reads that log and never re-runs the policy — which is what lets Zomboss's brain be
/// rewritten in a later wave without invalidating a single existing save. If the AI ran inside the
/// engine instead, every improvement to it would break every replay ever recorded.
///
/// It reads <see cref="IWorldView"/> and nothing else. The view already carries the faction and the
/// turn, so neither is a parameter here: there is then no way to hand a policy one faction's belief
/// and have it act as another, and no way for the turn it thinks it is to disagree with the turn its
/// belief came from.
/// </summary>
public interface IFactionPolicy
{
    /// <summary>The id a faction's <c>PolicyId</c> names. Stable — it is inside the state hash.</summary>
    string PolicyId { get; }

    /// <summary>
    /// Pure in `(belief, seed)`. Must return at most one order per entity, in stable ordinal order,
    /// and must never throw for a well-formed world — a policy that throws rolls back the commit
    /// that called it, deliberately, rather than being caught and quietly doing nothing.
    /// </summary>
    IReadOnlyList<PolicyOrder> Decide(IWorldView view, ulong seed);
}
