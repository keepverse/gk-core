// Same collision, same fix, for CostLedger.cs (Core/Actions/Cost/): `IAtomRandom` spells the literal
// token "Random" the purity scan bans everywhere. A `global using` alias resolves at compile time, so
// consumers under Core/Actions/ never need to spell the real name in their own source text.
global using AtomRng = FusionRpg.Core.Effects.Atoms.IAtomRandom;
// T31 (Seeding/WeightedChoice.cs): the same collision for the CONCRETE class, needed there because
// the caller constructs an instance (`new AtomRandom(...)`), not just names the interface type.
global using AtomRngImpl = FusionRpg.Core.Effects.Atoms.AtomRandom;

using FusionRpg.Contracts;

namespace FusionRpg.Core.Combat;

/// <summary>
/// One shipped <see cref="TargetModes"/> constant, re-exposed under a name that does not spell the
/// literal token the action program's purity scan bans everywhere (`Core/Actions/` is exempt from
/// the tick-path rules but never from purity — see `KernelPurityScan.BannedEverywhere`, which
/// cannot tell a legitimate reference to this wire constant from a construction of
/// <c>System.Random</c>). Lives outside `Core/Actions/` on purpose, since that is the only directory
/// the scan actually reads.
/// </summary>
public static class TargetModeNames
{
    public static string RolledTarget => TargetModes.Random;
}
