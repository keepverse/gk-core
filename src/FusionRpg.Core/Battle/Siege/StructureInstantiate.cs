using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Power;
using FusionRpg.Core.World;

namespace FusionRpg.Core.Battle.Siege;

/// <summary>
/// base-defense `structure-instantiate` (module 26, spec-structure-instantiate.md). **A wiring
/// module — the roll itself is <see cref="Instantiator.TryInstantiate"/>'s, unchanged. Never a
/// second roll (seedsmith Law 1).**
///
/// <para><b>The container is optional, and that is correct, not a workaround.</b> Every one of
/// `structure-corpus`'s 25 real rows has <see cref="StructureDef.ContainerId"/> null today —
/// giving a structure real traits/actions is `structure-planner`/`structure-pipeline`'s (27/28)
/// own content decision, verified by reading both specs directly (neither names a container/atom
/// decision anywhere). §1's own table is why: HP, cost, build turns, footprint, reach, cover and
/// vision are ALL plain <see cref="StructureDef"/> magnitudes/ordinals, never rolled — a structure's
/// behaviour is fully expressed without a container until one is actually authored. So a null
/// `ContainerId` correctly instantiates to "nothing rolled," never an error — and the call site
/// below is real production code, reachable the moment any structure (present or future) is given a
/// real one, exactly as spec success criterion 1 ("Instantiator.TryInstantiate has its first
/// production caller") asks for, proven end-to-end with a real container in this module's own
/// tests.</para>
/// </summary>
public static class StructureInstantiate
{
    /// <summary>
    /// Deterministic per-placement roll seed (§2: "never a clock or a counter, or replay breaks").
    /// Reuses <see cref="SeededRng.DeriveStream"/> — the SAME mixer every other per-context stream
    /// in this codebase already derives from, not a second one.
    /// </summary>
    public static ulong DeriveRollSeed(ulong worldSeed, string sectorId, int slotIndex, long buildTurn) =>
        SeededRng.DeriveStream(worldSeed, $"structure.{sectorId}.{slotIndex}.{buildTurn}").NextULong();

    /// <summary>
    /// Rolls one concrete structure instance. `def.ContainerId is null` → returns `true` with a
    /// real, empty (zero-atom) <paramref name="instance"/> — §1's own table names HP/cost/etc. as
    /// NEVER rolled, and today no structure has any trait/action content TO roll, so an empty
    /// result is the correct outcome, not a rejection. `def.ContainerId` naming a real but
    /// UNRESOLVABLE id is a content-authoring bug, not a runtime maybe — it throws, matching this
    /// program's own loud-over-silent catalog discipline.
    /// </summary>
    public static bool TryInstantiateStructure(
        StructureDef def,
        Func<string, ContainerRow?> lookupContainer,
        Func<string, AtomRow?> lookupAtom,
        Func<string, AffixRow?> lookupAffix,
        ulong rollSeed,
        int thetaContent,
        PowerTuning tuning,
        out InstanceRow? instance)
    {
        if (string.IsNullOrEmpty(def.ContainerId))
        {
            instance = new InstanceRow
            {
                ContainerId = "",
                RollSeed = unchecked((long)rollSeed),
                ThetaContent = thetaContent,
                ContentScaleMilli = 1000,
                Origin = InstanceOrigin.Craft,
            };
            return true;
        }

        var container = lookupContainer(def.ContainerId) ?? throw new InvalidOperationException(
            $"structure '{def.StructureId}' names container '{def.ContainerId}' which does not exist");

        var rejection = Instantiator.TryInstantiate(
            container, lookupAtom, lookupAffix, unchecked((long)rollSeed), thetaContent, tuning,
            out instance, InstanceOrigin.Craft);
        return rejection.IsOk;
    }
}
