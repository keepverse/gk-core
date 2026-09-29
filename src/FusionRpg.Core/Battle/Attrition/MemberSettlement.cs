namespace FusionRpg.Core.Battle.Attrition;

/// <summary>
/// What happens to a fielded member after a fight resolves. Engine vocabulary, not a delve concept.
/// </summary>
/// <remarks>
/// `RecoverDelves` is meaningful only for <see cref="Recover"/> — zero for the other two, never a
/// separate nullable field, since a tunable recovery count is never legitimately zero itself.
/// </remarks>
public enum SettlementOutcome { Roster, Recover, Retire }

/// <summary>One member's full settlement: the roster outcome and the loyalty `won` bit, decided
/// together because a caller settling one member needs both.</summary>
public sealed record MemberSettlement(SettlementOutcome Outcome, int RecoverDelves, bool Won);

/// <summary>
/// `solid-remediation` T4.9 — a mode's permadeath ladder, as an input the engine asks for rather than a
/// fact it is handed.
///
/// <para><b>Why an interface and not the `bool` this replaced.</b> A bool has a value that means "no
/// permanent death ever", and it is the value you get by default. A mode that had not thought about
/// its ladder passed `false` and permadeath silently never triggered — indistinguishable, at every
/// call site and in every test, from a mode that had decided permadeath does not apply. That is the
/// task's own note: <i>a silent zero is how the lawn got here</i>.</para>
///
/// <para>The engine therefore takes a ladder, and <b>null refuses</b>. A mode that has not decided
/// cannot compile its way past the question, and a mode that genuinely has no permanent death says so
/// out loud with <see cref="NeverPermadeath"/> — the same answer, but now a recorded decision instead
/// of an absence.</para>
///
/// <para><b>Mode inputs stay in the mode.</b> The implementation has already resolved whatever it
/// needs — the delve's rung and domain, the lawn's damage taken — so the engine never learns a mode's
/// vocabulary. That is the same separation T4.8 asserted structurally, kept by construction here.</para>
/// </summary>
public interface IPermadeathLadder
{
    /// <summary>Does permanent death apply to the member being settled right now?</summary>
    bool PermadeathApplies();
}

/// <summary>A mode that genuinely has no permanent death, stated rather than defaulted. Using this is
/// a decision a reader can see; passing a bare `false` was not.</summary>
public sealed class NeverPermadeath : IPermadeathLadder
{
    public static readonly NeverPermadeath Instance = new();
    public bool PermadeathApplies() => false;
}

/// <summary>
/// <b>The lawn's CURRENT behaviour, stated out loud — not an endorsement of it</b>
/// (`solid-remediation`, 2026-09-17).
///
/// <para><b>What this records.</b> Every unique actor that dies on the lawn is permanently retired,
/// with its rolled gear moved to the corpse cache. That is not a design decision anyone wrote down; it
/// is what the lawn die path does because it inlined the delve's own <c>case Retire:</c> branch instead
/// of asking <see cref="MemberSettlementRules.Decide"/> which outcome applies. Routing that call site
/// through the seam with this ladder is <b>byte-identical</b> — <c>downedOnce: true</c> plus a ladder
/// that returns <c>true</c> yields exactly <see cref="SettlementOutcome.Retire"/> — while removing a
/// second, invisible decision path for a decision the engine already owns.</para>
///
/// <para><b>This is the same argument <see cref="NeverPermadeath"/> makes, in the other direction.</b>
/// That type exists because a silent <c>false</c> was indistinguishable from a mode that had decided.
/// A silent, inlined <c>Retire</c> is indistinguishable from a mode that had decided permanent death is
/// always right — so it gets a name too, and a reader can now see the claim and dispute it.</para>
///
/// <para><b>It is expected to be replaced, and the replacement already exists.</b>
/// <c>LawnPermadeathLadder</c> and <c>gk-core/data/tuning/lawn-attrition.v2.json</c> ship a real, tuned curve in
/// which a lawn death is only <i>sometimes</i> permanent, scaled by damage taken. Swapping this type for
/// that one is a one-line change at the call site, and it is blocked on two things that are not this
/// type's to settle: the lawn die event carries <b>no per-actor damage-taken figure</b> for the curve to
/// read, and moving from "always permanent" to "sometimes permanent" is a <b>balance decision</b> with a
/// real player-visible effect. Registered as <c>SR-17</c>.</para>
/// </summary>
public sealed class AlwaysPermadeath : IPermadeathLadder
{
    public static readonly AlwaysPermadeath Instance = new();
    public bool PermadeathApplies() => true;
}

/// <summary>
/// `solid-remediation` T4.8 (D10) — death and injury decided as **engine vocabulary**, promoted out of
/// `Core/Delve/Attrition/` on 2026-09-17.
///
/// <para><b>Promotion, not construction.</b> The rule below is byte-for-byte the delve's own, and
/// injury was already an `ActorHub` subsystem — what was wrong is that the *decision* of whether a
/// downed member is rostered, recovering or retired lived in one mode, so every other mode either had
/// no answer or would have grown a second one. D10 is that ownership, not a missing mechanism.</para>
///
/// <para><b>The difficulty ladder stays the delve's.</b> <paramref name="permadeathApplies"/> arrives
/// already resolved — this type never touches `difficulty-ladder`'s own types, which is what lets the
/// delve keep its ladder unchanged while the decision moves. That separation already existed in the
/// delve's version and is the reason this promotion is a move rather than a redesign; a mode with a
/// different ladder, or none, answers the same question with its own input.</para>
///
/// <para><b>What deliberately did NOT move:</b> `PartyStands` and `IsWiped`. They read
/// `DelveMemberState` — a delve party shape, not an engine one — so promoting them would drag a mode's
/// vocabulary into the engine, which is the defect D10 names, pointing the other way.</para>
/// </summary>
public static class MemberSettlementRules
{
    /// <summary>
    /// <paramref name="downedOnce"/> is the CALLER's own effective value — on a wipe the caller passes
    /// <c>true</c> for every member regardless of that member's own history; this function knows
    /// nothing about wipes, only the one flag. <paramref name="afflicted"/> is whether the member's
    /// nerve stage is the top one right now, resolved by the caller for the same reason.
    /// <paramref name="extracted"/>/<paramref name="bossKilled"/>/
    /// <paramref name="routeAtLeastHalfCleared"/> are run-level facts, identical for every member of
    /// the call.
    /// </summary>
    public static MemberSettlement Decide(
        bool downedOnce,
        IPermadeathLadder ladder,
        int downedRecoveryDelves,
        bool afflicted,
        bool extracted,
        bool bossKilled,
        bool routeAtLeastHalfCleared)
    {
        // T4.9: one call site, and it refuses a mode that never decided. `NeverPermadeath.Instance`
        // is the explicit "this mode has none" answer — the same outcome, but written down.
        if (ladder is null)
            throw new ArgumentNullException(nameof(ladder),
                "a mode must supply its permadeath ladder — pass NeverPermadeath.Instance to say it has "
                + "none, rather than leaving permanent death to never trigger silently");

        var outcome = !downedOnce ? SettlementOutcome.Roster
            : ladder.PermadeathApplies() ? SettlementOutcome.Retire
            : SettlementOutcome.Recover;

        int recoverDelves;
        if (outcome == SettlementOutcome.Recover)
        {
            if (downedRecoveryDelves <= 0)
                throw new ArgumentOutOfRangeException(nameof(downedRecoveryDelves), downedRecoveryDelves, "a recovery count is never zero or negative");
            recoverDelves = downedRecoveryDelves;
        }
        else
        {
            recoverDelves = 0;
        }

        // "won when the run was completed AND (the boss was killed OR the party cleared at least half
        // the rooms on its route) AND the member is not afflicted at the end."
        var won = extracted && (bossKilled || routeAtLeastHalfCleared) && !afflicted;

        return new MemberSettlement(outcome, recoverDelves, won);
    }
}
