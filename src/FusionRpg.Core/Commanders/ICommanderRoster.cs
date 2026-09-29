using FusionRpg.Core.Saves;

namespace FusionRpg.Core.Commanders;

/// <summary>`commander-roster` EP3.2 — an empire's commanders, enumerated (spec-commander-roster.md
/// "Listing — a separate interface (I in SOLID)").
///
/// <para>Its own interface rather than a sixth method on <see cref="ICommanderDirectory"/>, because most
/// directory consumers (allocation, kill attribution) never list and should not depend on a method they
/// do not use. Resolution stays on the directory: this answers "who is in the roster", never "what is
/// this stable id".</para>
/// </summary>
public interface ICommanderRoster
{
    /// <summary>The empire's commanders: the authored default(s) for that empire, then every creature
    /// holding the role, ordinal by stable id.</summary>
    IReadOnlyList<CommanderRef> ForEmpire(EmpireRef empire);
}
