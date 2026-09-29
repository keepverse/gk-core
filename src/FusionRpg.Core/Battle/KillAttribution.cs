using FusionRpg.Core.Commanders;
using FusionRpg.Core.Match;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;

namespace FusionRpg.Core.Battle;

/// <summary>
/// Who a kill is credited to. <see cref="Empire"/> is always present — that is the whole point of this
/// type, and the property D9 was missing. <see cref="SpecimenOwner"/> is present only when the killer is
/// a registered specimen, because only then does a save/empire row exist to name.
///
/// <para><b>save-identity SE4.27:</b> was a bare <c>long? PlayerId</c> — a save id alone no longer says
/// which empire of that save owns the kill (after SE4.22, Zomboss's specimen shares its save's row with
/// the human). <see cref="EmpireRef"/> names both.</para>
/// </summary>
/// <param name="Empire">The army the killer fights for. Total: every board entity has one.</param>
/// <param name="SpecimenOwner">
/// The owning save's empire, when the killer is a registered specimen. Null for the general horde and
/// for any vanilla entity — those belong to an empire without belonging to a specimen, which is exactly
/// the distinction that used to collapse into "no owner at all".
/// </param>
public readonly record struct KillCredit(EmpireId Empire, EmpireRef? SpecimenOwner);

/// <summary>
/// `solid-remediation` T4.3 (D9) — the owner of a kill, in every mode.
///
/// <para><b>The defect this closes.</b> D9: *"kill attribution is absent outside the lawn, and on the
/// lawn the general horde has no owner row, so no empire earns from it"*
/// (<c>MatchHost.cs:322-324</c>). The lawn's ownership model is
/// <see cref="SpecimenOwnershipOracle"/>, which answers `ptr → owning player id` and returns **null**
/// for anything not registered as a specimen. Every vanilla zombie in a wave is unregistered, so the
/// horde resolved to null and `MatchHost` skipped it outright. Null is not "unowned" as a fact about
/// the game — it is the absence of a *player row*, which the code then read as the absence of an
/// *owner*. Those are different questions, and conflating them is what left an entire army belonging
/// to nobody.</para>
///
/// <para><b>The rule: the empire is the army you fight for, not the row that owns you.</b> A vanilla
/// zombie IS Zomboss's own army and a vanilla plant is the player's — <c>MatchHost</c>'s own comment
/// has said so since the Zomboss-deploy work; nothing read it as ownership. So the empire is a total
/// function of the **effective** mechanical side, and it is never null for a real entity. Mind control
/// flips which side an entity fights for, and therefore which empire earns from what it kills —
/// the same reading <see cref="MechanicalOwnSideOracle"/> already applies for grants.</para>
///
/// <para><b>Why the player row cannot decide it.</b> Before save-identity SE4.22, Zomboss owned a real,
/// distinct player row (`RpgStore.EnsureZombossPlayer`, zomboss-deploy-ai T3.4); after it, no player row
/// represents Zomboss at all — his specimens mint straight onto the match's own save, the SAME row the
/// human plays. Either way "specimen ownership resolved a player id" does not mean "the human player":
/// deriving the empire from the row would credit Zomboss's own deployed specimen to Dave for every kill
/// it makes (now doubly so, since the row is literally shared). Side is the honest signal; the player
/// row refines *which player within that empire*, and only when one exists.</para>
///
/// <para><b>One mapping, not two.</b> Side to empire is <see cref="SpeciesAllocation.EmpireForSide"/> —
/// the SSOT T4.1 established for the species scope key. A second copy of "zombies are Zomboss's" is
/// exactly the parallel-path defect this program exists to remove, and the two would drift the day a
/// third empire ships.</para>
/// </summary>
public static class KillAttribution
{
    /// <summary>
    /// The empire an entity fights for. Total over the closed <see cref="StatSide"/> vocabulary — there
    /// is no input for which this has no answer, which is the closure property D9 asks for.
    /// </summary>
    public static EmpireId EmpireOf(StatSide side, bool mindControlled = false) =>
        SpeciesAllocation.EmpireForSide(mindControlled ? Opposite(side) : side);

    /// <summary>
    /// Credit for a kill made by an entity on <paramref name="side"/>. The empire always resolves; the
    /// specimen's owner is carried through only when the killer is a registered specimen.
    /// </summary>
    public static KillCredit Credit(StatSide side, bool mindControlled = false, EmpireRef? specimenOwner = null) =>
        new(EmpireOf(side, mindControlled), specimenOwner);

    /// <summary>
    /// The board form. <see cref="BoardSide.Bullet"/> <b>throws</b>: a projectile is not an army and
    /// never earns — the same entity <c>MatchHost</c> already skips before asking about ownership.
    /// </summary>
    public static EmpireId EmpireOf(BoardSide side, bool mindControlled = false) => side switch
    {
        BoardSide.Plant => EmpireOf(StatSide.Plant, mindControlled),
        BoardSide.Zombie => EmpireOf(StatSide.Zombie, mindControlled),
        _ => throw new ArgumentOutOfRangeException(
            nameof(side), side,
            "a bullet is a transient projectile, not a member of an army — it has no empire to credit"),
    };

    /// <summary>
    /// The battle/board string form (`"plant"` / `"zombie"` — the bare tokens
    /// <see cref="MechanicalOwnSideOracle"/>, <c>CreatureSpeciesDef.Side</c> and
    /// <c>BattleActorSetup.Side</c> all already use; this repo has no constant class for them).
    ///
    /// <para>An unrecognised side <b>throws</b> rather than defaulting to an empire. A silent default
    /// here would re-create D9 in a new shape: every unknown entity quietly earning for one empire, with
    /// no symptom to notice. Callers on the closed <see cref="StatSide"/> enum never reach this.</para>
    /// </summary>
    public static EmpireId EmpireOf(string side, bool mindControlled = false) =>
        EmpireOf(ParseSide(side), mindControlled);

    /// <inheritdoc cref="Credit(StatSide, bool, EmpireRef?)"/>
    public static KillCredit Credit(string side, bool mindControlled = false, EmpireRef? specimenOwner = null) =>
        Credit(ParseSide(side), mindControlled, specimenOwner);

    /// <summary>
    /// The BATTLE form. <c>BattleActorSetup.Side</c> is `"squad" | "wave"</c>
    /// (<c>BattleModels.cs:10</c>) — a different vocabulary from the lawn's `"plant" | "zombie"`, which
    /// is why a single string overload would have silently covered neither. Every registered mode
    /// (classic-round, galaxy-sync, hybrid-atb, siege, delve) builds its actors with these tokens, so
    /// this is the entry point "attribution in every mode" actually goes through.
    ///
    /// <para>The squad/wave to side mapping is <c>BattleHubCompose</c>'s
    /// (<c>"wave"</c> is the zombie side, squad is the plant side) rather than a second reading of the
    /// same tokens. It differs in exactly one deliberate way: <c>BattleHubCompose</c> treats anything
    /// that is not <c>"wave"</c> as squad, because a compose must produce a number; attribution
    /// <b>refuses</b> an unknown token instead, because crediting the wrong empire is worse than
    /// failing loudly — that is the whole shape of D9.</para>
    /// </summary>
    public static KillCredit CreditForBattleSide(
        string battleSide, bool mindControlled = false, EmpireRef? specimenOwner = null) =>
        Credit(ParseBattleSide(battleSide), mindControlled, specimenOwner);

    /// <inheritdoc cref="CreditForBattleSide(string, bool, EmpireRef?)"/>
    public static EmpireId EmpireOfBattleSide(string battleSide, bool mindControlled = false) =>
        EmpireOf(ParseBattleSide(battleSide), mindControlled);

    static StatSide ParseBattleSide(string battleSide) => battleSide switch
    {
        "squad" => StatSide.Plant,
        "wave" => StatSide.Zombie,
        _ => throw new ArgumentOutOfRangeException(
            nameof(battleSide), battleSide,
            $"unknown battle side '{battleSide}' — a kill must credit exactly one empire, so an " +
            "unrecognised token is refused rather than defaulted (expected 'squad' or 'wave')"),
    };

    static StatSide ParseSide(string side) => side switch
    {
        "plant" => StatSide.Plant,
        "zombie" => StatSide.Zombie,
        _ => throw new ArgumentOutOfRangeException(
            nameof(side), side,
            $"unknown side '{side}' — a kill must credit exactly one empire, so an unrecognised side is " +
            "refused rather than defaulted (expected 'plant' or 'zombie')"),
    };

    static StatSide Opposite(StatSide side) =>
        side == StatSide.Plant ? StatSide.Zombie : StatSide.Plant;
}
