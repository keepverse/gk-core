using FusionRpg.Contracts;
using FusionRpg.Core.Battle;

namespace FusionRpg.Core.Actions.Ai.Lawn;

/// <summary>
/// combat-ai `lawn-actor-view` (CAI4.1, spec-lawn-actor-view.md "The relation chain"): an
/// <see cref="IOwnSideOracle"/> that asks the shipped oracles in order and returns the FIRST non-null
/// answer. It is not a third oracle — it composes <see cref="SpecimenOwnershipOracle"/> and
/// <see cref="MechanicalOwnSideOracle"/> without re-deciding what either one means, so a change to
/// either oracle's own rule reaches the lawn through this one seam.
///
/// <para><b>Specimen-first, and the ORDER is what this class owns.</b> "Which player deployed it"
/// outranks "which mechanical side is it on" whenever both can answer — that is the whole point of the
/// composition, because hypnosis makes them disagree: a unique creature the player deployed but which
/// sits on the zombie side resolves <c>Ally</c> to the specimen oracle and <c>Enemy</c> to the
/// mechanical one, and both answers are individually correct.
/// <paramref name="specimenOwnership"/> is therefore the FIRST parameter and
/// <paramref name="mechanicalOwnSide"/> the second, both by name at every call site: an array would
/// leave the precedence to positional convention and a future third link to whoever appends it, which
/// is how a tie-break order silently inverts.</para>
///
/// <para><b>Null means unknown, not "enemy".</b> A raw vanilla unit has no owner row and no mechanical
/// read, so both links return null and this returns null; the caller resolves that
/// (<c>LawnUnitViewFactory.Build</c> → <see cref="RelationKind.Enemy"/>) — "never silently dropped,
/// never mis-read as Self/Ally", that factory's own words. Defaulting here would hide the difference
/// between "not mine" and "I cannot tell".</para>
/// </summary>
public sealed class LawnRelationChain : IOwnSideOracle
{
    readonly IOwnSideOracle _specimenOwnership;
    readonly IOwnSideOracle _mechanicalOwnSide;

    public LawnRelationChain(IOwnSideOracle specimenOwnership, IOwnSideOracle mechanicalOwnSide)
    {
        _specimenOwnership = specimenOwnership ?? throw new ArgumentNullException(nameof(specimenOwnership));
        _mechanicalOwnSide = mechanicalOwnSide ?? throw new ArgumentNullException(nameof(mechanicalOwnSide));
    }

    /// <summary>The first non-null answer, in the stated order. Short-circuits: a specimen answer never
    /// asks the mechanical oracle at all, so a second read cannot contradict the first.</summary>
    public RelationKind? RelationOf(string ptr)
    {
        if (ptr is null) throw new ArgumentNullException(nameof(ptr));

        return _specimenOwnership.RelationOf(ptr) ?? _mechanicalOwnSide.RelationOf(ptr);
    }
}
