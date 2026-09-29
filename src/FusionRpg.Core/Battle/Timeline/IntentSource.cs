namespace FusionRpg.Core.Battle.Timeline;

/// <summary>
/// What an actor has decided to do. The kernel treats this as opaque beyond its timing envelope —
/// what the action *does* belongs to the combat action program.
///
/// A <b>struct</b>, deliberately: the kernel ticks inside the Unity frame, so a class here would
/// be one heap object per actor per turn, every turn, on a board that can hold 200+ entities.
/// <see cref="Envelope"/> stays a reference because envelopes are content — authored once and
/// shared, never built per turn.
/// </summary>
public readonly record struct ActionIntent(string ActionId, string? TargetKey, ActionEnvelope Envelope)
{
    /// <summary>No declaration. Distinguishes "chose nothing" without allocating a null wrapper.</summary>
    public bool IsNone => Envelope is null;

    public static readonly ActionIntent None = default;
}

/// <summary>
/// Where intents come from. One interface serves both masters: it is the AI-policy seam the
/// auto-resolved modes need, and the player-input seam an interactive mode needs.
///
/// Returning null means "no legal action right now" — which the kernel must handle explicitly.
/// Gating `Ready -> Committed` on an intent while having no defined behavior for its absence is a
/// deadlock: under next-event advance an actor holding a slot with nothing scheduled drains the
/// queue and stops the clock.
/// </summary>
public interface IIntentSource
{
    /// <summary>
    /// Returns <see cref="ActionIntent.None"/> when the actor has no legal action. A
    /// <c>Nullable&lt;ActionIntent&gt;</c> would re-introduce the per-call wrapper the struct exists
    /// to avoid, so absence is encoded in the value itself.
    /// </summary>
    ActionIntent TryDeclare(string actorKey, long nowTick);
}

/// <summary>combat-ai `intent-router` (module 4, CAI1.10): the identity/placeholder source — always
/// declares nothing. `IntentRouter.Compose`'s own `policy` parameter is mandatory
/// (spec-intent-router.md §1a's own signature), so a caller with no real automated policy to supply
/// (every non-siege battle today, since `state.DefaultAiIntentSource` is null there) hands this
/// instead of inventing a null-check the router itself would otherwise need — matching
/// <see cref="NoStanceHeld"/>/<see cref="AlwaysAffordable"/>'s own "no real implementation yet, exactly
/// one shared no-op singleton" precedent.</summary>
public sealed class NoneIntentSource : IIntentSource
{
    public static readonly NoneIntentSource Instance = new();
    public ActionIntent TryDeclare(string actorKey, long nowTick) => ActionIntent.None;
}

/// <summary>What the kernel decided to do with an actor that reached <see cref="TurnState.Ready"/>.</summary>
public enum SeatOutcome
{
    /// <summary>Intent declared and a slot taken — the actor is committing.</summary>
    Seated,
    /// <summary>No slot free. The actor stays Ready and contends again when one frees.</summary>
    NoSlot,
    /// <summary>No legal intent. The actor takes no slot and reschedules — never blocks the clock.</summary>
    Passed,
    /// <summary>Crowd-controlled — a derived read from StatusRuntime, never stored on the machine.</summary>
    Incapacitated
}

public readonly record struct SeatResult(SeatOutcome Outcome, ActionIntent Intent);
