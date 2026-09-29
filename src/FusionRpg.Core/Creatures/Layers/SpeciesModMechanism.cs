namespace FusionRpg.Core.Creatures.Layers;

/// <summary>
/// The closed vocabulary of ways an empire's species can be modified — layer 1b. **Closed on purpose**
/// (spec-species-mod-ledger.md): a second mechanism is a reviewed change, because a new writer of 1b is a
/// new way to change a species for a player. Debug reforge is deliberately <b>not</b> a member: it wrote
/// eager rolls, and there are none left.
/// </summary>
public enum SpeciesModMechanism
{
    FusionPick,
}

public static class SpeciesModMechanisms
{
    /// <summary>The persisted token. A mechanism's row is written under this string, so it never changes.</summary>
    public static string Token(this SpeciesModMechanism mechanism) => mechanism switch
    {
        SpeciesModMechanism.FusionPick => "fusion-pick",
        _ => throw new ArgumentOutOfRangeException(
            nameof(mechanism), mechanism, "unknown species mod mechanism"),
    };
}
