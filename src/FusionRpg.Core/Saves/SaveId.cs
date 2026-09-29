using FusionRpg.Core.Commanders;

namespace FusionRpg.Core.Saves;

/// <summary>
/// A save: one playthrough. Its value is <c>players.id</c> (ruling R17 — the player row <b>is</b> the
/// save, so no id is rewritten). Never an empire, never a person.
/// </summary>
public readonly record struct SaveId(long Value)
{
    public override string ToString() => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// The owner of every empire-scoped row: an empire <b>of a save</b>. Zomboss in save 1 and Zomboss in
/// save 2 are two different owners, which is the whole of ruling R3.
/// </summary>
public readonly record struct EmpireRef(SaveId Save, EmpireId Empire)
{
    public override string ToString() => $"{Save.Value}:{Empire.Value}";
}

/// <summary>
/// Who decides for an empire. <b>Closed on purpose</b> (spec-save-identity.md "Types"): the two members
/// are the contract, and a third kind of decider is a design change with a ruling, not a new value.
/// </summary>
public enum EmpireController
{
    Human,
    Ai,
}
