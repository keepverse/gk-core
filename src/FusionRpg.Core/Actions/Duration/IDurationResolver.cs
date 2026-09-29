namespace FusionRpg.Core.Actions.Duration;

/// <summary>
/// T28 (spec-duration-resolver.md §4): "how long" resolved per mode, behind one interface, so
/// authored content never changes when a new mode's answer arrives — "author once, resolve per
/// mode", the same shape <c>Relation</c> already uses compiling to <c>TargetSpec[2]</c>.
///
/// <para><b><c>victimPtr</c>, not <c>ActorRef</c>:</b> the spec's own pseudocode names a type
/// (<c>ActorRef</c>) that does not exist anywhere in this codebase — verified by search, not assumed
/// absent. Every other seam in this program that names an actor (<c>StatusRuntime</c>'s
/// <c>HostPtr</c>/<c>AttackerPtr</c>, <c>StanceRuntime</c>'s <c>actorKey</c>) uses a bare
/// <c>string</c> pointer, so this seam matches that established convention instead of inventing the
/// missing type.</para>
///
/// <para><b>One method</b> — spec §4 names it directly ("one method, one implementation per mode").
/// Takes an already-resolved integer turn count, never a raw authored value or a float: any
/// duration-net-factor scaling and the clamp-and-convert (<see cref="DurationClamp"/>) both happen
/// BEFORE a caller reaches this seam, so nothing here ever sees the intermediate fraction.</para>
/// </summary>
public interface IDurationResolver
{
    /// <summary>Resolves <paramref name="victimTurns"/> — an authored/clamped control-duration turn
    /// count — to a tick count for <paramref name="victimPtr"/>'s own cadence. `Θ`-free and mode-free
    /// by construction (spec §1): the same authored turn count means the same thing everywhere this
    /// is called, and nothing about the caller's power level ever enters this method.</summary>
    long ToTicks(int victimTurns, string victimPtr);
}
