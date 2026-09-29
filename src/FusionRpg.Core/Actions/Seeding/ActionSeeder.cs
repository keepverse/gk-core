using System.Linq;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions.Seeding;

public sealed class ActionSeedingRejection : Exception
{
    public ActionSeedingRejection(string message) : base(message) { }
}

/// <summary>One generated action, not yet bound: atoms picked, a target shape rolled, a name
/// composed. Binding it into a real <see cref="ActionRow"/> and running it through
/// <see cref="ActionCompiler"/> is a separate, later step — this type carries only what §2's "rolled
/// at runtime" column produces.</summary>
public sealed record SeededAction(IReadOnlyList<string> AtomIds, ActionTargetSpec Targeting, string Name);

/// <summary>
/// T31 (spec-action-seeding.md): the runtime generator — "the generator already exists" for the atom
/// half (<see cref="Instantiator.Draw"/>, unchanged, only its visibility widened); this class is the
/// thin action-specific layer around it: seed → pool (reused) → atoms (reused) → target shape (new,
/// <see cref="WeightedChoice"/>) → composed name (new, <see cref="ActionNameTemplates"/>).
///
/// <para><b>The shape pool is board-gated</b> (spec §4): an <c>Area</c> candidate is never even
/// eligible to be rolled while no board exists, rather than relying solely on
/// <c>ActionValidator</c>'s later bind-time <c>AreaRequiresBoard</c> rejection to catch it after the
/// fact — both are real: the pool gate stops it from ever being rolled, and the bind-time check is
/// what still refuses it if a caller supplies <paramref name="boardAvailable"/> incorrectly or hands
/// a hand-authored <c>Area</c> spec straight to <c>ActionCompiler</c>.</para>
/// </summary>
public static class ActionSeeder
{
    public static SeededAction Generate(
        ContainerRow container,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        long rollSeed,
        IReadOnlyList<WeightedOption<ActionTargetSpec>> targetShapePool,
        bool boardAvailable,
        ActionNameTemplates nameTemplates)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(lookupAtom);
        ArgumentNullException.ThrowIfNull(lookupAffix);
        ArgumentNullException.ThrowIfNull(targetShapePool);
        ArgumentNullException.ThrowIfNull(nameTemplates);

        var atomIds = Instantiator.Draw(container, lookupAtom, lookupAffix, rollSeed);
        if (atomIds.Count == 0)
            throw new ActionSeedingRejection($"container '{container.ContainerId}' drew zero atoms — nothing to name or target");

        var eligibleShapes = boardAvailable
            ? targetShapePool
            : targetShapePool.Where(o => o.Value.Mode != ActionTargetMode.Area).ToList();

        var targeting = WeightedChoice.Pick(eligibleShapes, rollSeed, "shape." + container.ContainerId);

        var familyIds = new List<string>(atomIds.Count);
        foreach (var atomId in atomIds)
        {
            var atom = lookupAtom(atomId) ?? throw new ActionSeedingRejection($"drew unknown atom id '{atomId}'");
            familyIds.Add(atom.FamilyId);
        }

        var name = nameTemplates.Compose(familyIds);

        return new SeededAction(atomIds, targeting, name);
    }
}
