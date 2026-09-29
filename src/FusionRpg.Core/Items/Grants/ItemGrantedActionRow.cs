using System.Text.RegularExpressions;
using FusionRpg.Core.Actions.Grants;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Grants;

/// <summary>
/// ssot-granted-actions.md §5.2's closed <c>grant_role</c> set. Two values and there will never be a
/// third from this side: a role that answered <i>when</i>, <i>how much</i>, <i>at whom</i> or
/// <i>how often</i> would be §4.1 option (C) arriving under a different name.
/// </summary>
public enum ItemGrantRole
{
    /// <summary>An extra entry in the actor's selectable set. Legal on every equip role.</summary>
    Granted = 0,

    /// <summary>Replaces the species' intrinsic basic attack. §4.3 option (C): legal on
    /// <c>armament-primary</c> only, so the 1H + off-hand conflict is unrepresentable rather than
    /// arbitrated.</summary>
    DefaultAttack,
}

/// <summary>The closed two, and the wire spellings <c>item_granted_action.grant_role</c> carries.
/// ⭐ <c>default-attack</c> is NOT respelled here — <see cref="ActionGrantRoles.DefaultAttack"/> is the
/// shipped constant and this maps onto it, so the item side and the assembler cannot drift.</summary>
public static class ItemGrantRoles
{
    public const string Granted = "granted";

    public static readonly IReadOnlyList<ItemGrantRole> All = new[]
    {
        ItemGrantRole.Granted, ItemGrantRole.DefaultAttack,
    };

    public static string Wire(ItemGrantRole r) => r switch
    {
        ItemGrantRole.Granted => Granted,
        ItemGrantRole.DefaultAttack => ActionGrantRoles.DefaultAttack,
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };

    public static bool TryParse(string? id, out ItemGrantRole r)
    {
        if (string.Equals(id, Granted, StringComparison.Ordinal)) { r = ItemGrantRole.Granted; return true; }
        if (string.Equals(id, ActionGrantRoles.DefaultAttack, StringComparison.Ordinal))
        {
            r = ItemGrantRole.DefaultAttack;
            return true;
        }
        r = default;
        return false;
    }
}

/// <summary>
/// Structural limits of the grant seam. Each is <b>exempt</b> from AGENTS.md's no-hard-ceilings rule
/// and, as that rule requires, says here why — none of them is a magnitude and none of them is a
/// number a balance pass would move.
/// </summary>
public static class ItemGrantLimits
{
    /// <summary>
    /// <b>STRUCTURAL — the class's own definition, not a dial.</b> An actor has one basic attack, and
    /// §3.7(c)'s precedence has exactly two rungs (<c>armament-primary</c>'s replacement, else the
    /// species intrinsic). Two <c>default-attack</c> rows on one base type would need a third rung and
    /// an arbitration rule, which is precisely the thing option (C) was chosen to make unrepresentable.
    /// </summary>
    public const int MaxDefaultAttacksPerContainer = 1;

    /// <summary>
    /// <b>STRUCTURAL — §4.3 option (C).</b> The one equip role a <c>default-attack</c> may sit on. A
    /// tunable here would let a balance pass re-open the 1H + off-hand conflict by editing a file.
    /// </summary>
    public const string DefaultAttackRoleId = "armament-primary";

    /// <summary>
    /// <b>A BOUNDED RATIO, not a progression ceiling</b> (AGENTS.md's stated exemption). R2 prices a
    /// granted action as a per-mille SHARE of the item's own rarity ceiling, so 1000‰ is the ceiling
    /// itself expressed in the share's units — "this one action costs the item's entire budget". It is
    /// the identity of the unit, not a chosen number, and the configurable soft cap that may tighten it
    /// is <c>grantedActionShareCapMilli</c> in <c>gk-core/data/tuning/item-power.v1.json</c> (module 9's file,
    /// <c>null</c> today — no number is invented here).
    /// </summary>
    public const int WholeCeilingShareMilli = 1000;

    /// <summary>
    /// ⛔ <b>The granted-action COUNT cap is CLOSED, and the answer is "uncapped by design"</b> —
    /// verified 2026-09-05 against <see cref="CapPolicy"/> (action program, T24), which answers
    /// handshake item 8 by naming which existing cap governs rather than minting a new one:
    /// <c>HeldCap</c> is the levelling faucet, <c>EquippedSkillCap</c> is the real bottleneck, and
    /// "granted by paid sources" is deliberately uncapped ("an uncapped pool grows the choice, never
    /// the power"). §3.7(d)'s proposed cap of 8 and its <c>TooManyGrantedActions</c> code therefore
    /// have no raiser on either side of the seam, and this module mints neither.
    /// </summary>
    public const bool GrantedCountCapExists = false;

    /// <summary>The <c>container_id</c> prefix a base type carries — the same <c>item.</c> namespace
    /// module 17 derives a unique's container id into.</summary>
    public const string ContainerIdPrefix = "item";
}

/// <summary>
/// ⛔ Cross-program landed flags, mirroring <see cref="FusionRpg.Core.Actions.CrossProgramLandedFlags"/>
/// verbatim in shape: a <c>const bool</c> flipped by whoever lands the real feature, never a runtime
/// probe. Tests that cannot run yet <b>skip against a flag</b> rather than being silently absent.
/// </summary>
public static class ItemGrantLandedFlags
{
    /// <summary>
    /// ⛔ <b>X3 — nothing turns an action seed into a concrete <c>rpg_action</c> row.</b> Re-verified
    /// 2026-09-05, not assumed: every call to <c>ActionSeeder.Generate</c> is in
    /// <c>gk-core/tests/FusionRpg.Core.Tests/Actions/ActionSeedingTests.cs</c>, plus one doc-comment mention in
    /// <c>gk-core/tests/FusionRpg.Server.Tests/AtomEndToEndTests.cs</c>. No production path exists.
    ///
    /// <para>This is an ORDINARY external dependency owned by <c>action-corpus</c> (D36): we consume a
    /// production caller the day one exists, and we do not build one, amend their map, file a row in
    /// their program, or read their schedule to reason about ours. Gates GA3 and GA4 wait on it.</para>
    /// </summary>
    public const bool ActionCorpusProducerLanded = false;

    /// <summary>
    /// Handshake item 7's contract is written (<see cref="GrantRemovalPolicy"/>) and unreachable:
    /// equipment cannot change mid-run. <c>UniqueActorService.PutEquipment</c> refuses unless the
    /// actor's phase is <c>Roster</c> (<c>phase.not_roster</c>), and <c>ClearEquipment</c> routes
    /// through the same method — so unequip is refused on the same gate.
    /// </summary>
    public const bool MidRunEquipLanded = false;
}

/// <summary>
/// This module's content-rule namespace. item-ideal.md §2b.1: <b>one</b>
/// <see cref="AtomRejectionReason.ContentRuleViolated"/> code carrying a namespaced rule id, never a
/// new member of the closed enum.
///
/// <para>⛔ ssot-granted-actions.md §6.3 proposes <b>four new codes</b> — <c>UnknownAction</c>,
/// <c>ActionNotGrantable</c>, <c>DefaultAttackNotAllowed</c>, <c>TooManyGrantedActions</c> — taking the
/// closed list "from 33 to 37". This module mints <b>none</b>, the same call modules 11, 17 and 18 made
/// for their own proposed codes. ⚠ Note the action program's OWN enum
/// (<c>ActionRejectionReason</c>, a different vocabulary) already ships
/// <c>ActionNotGrantable</c> and <c>ActionNotDefaultAttackEligible</c>; those are the write-path's
/// refusals at <c>RpgStore.UpsertGrant</c> and are reused verbatim, not duplicated.</para>
/// </summary>
public static class ItemGrantRules
{
    public const string Namespace = "grant";

    /// <summary>§6.1: <c>action_id</c> names no row in <c>rpg_action</c>, or names one with
    /// <c>enabled = 0</c> — one rule for both clauses, exactly as the lane specifies.</summary>
    public const string UnknownAction = "grant.unknown-action";

    /// <summary>§6.1: the action exists but is not flagged <c>grantable</c> (handshake item 2).</summary>
    public const string NotGrantable = "grant.not-grantable";

    /// <summary>The action is a <c>Basic</c>. Every actor already has its three basics intrinsically,
    /// so a grant naming one would double-count it and make "is this intrinsic" depend on whether a
    /// grant happened to exist — the shipped <c>ActionValidator.ValidateGrant</c> refuses it at the
    /// write; this refuses it one step earlier, at import.</summary>
    public const string BasicCollision = "grant.basic-collision";

    /// <summary>§6.1's <c>DefaultAttackNotAllowed</c>, all three clauses: a role other than
    /// <c>armament-primary</c>, an action not flagged <c>default_attack_eligible</c>, or two
    /// <c>default-attack</c> rows on one base type.</summary>
    public const string DefaultAttackNotAllowed = "grant.default-attack-not-allowed";

    /// <summary>§6.1's <c>UnknownContainer</c> clauses: the container id is malformed, or the container
    /// is not <see cref="ContainerKind.Item"/>.</summary>
    public const string UnknownContainer = "grant.unknown-container";

    /// <summary>§6.1's <c>DuplicateSeq</c>: two rows share a <c>(container_id, seq)</c>.</summary>
    public const string DuplicateSeq = "grant.duplicate-seq";

    /// <summary>§6.1's <c>DuplicateKey</c>: the same <c>action_id</c> twice on one base type. Two
    /// ITEMS granting one action is legal and dedups; one item declaring it twice is a row error.</summary>
    public const string DuplicateAction = "grant.duplicate-action";

    /// <summary>§6.1's <c>BadParamValue</c>: <c>grant_role</c> outside the closed set, or a negative
    /// <c>seq</c>.</summary>
    public const string BadValue = "grant.bad-value";

    /// <summary>⭐ R2 (spec-item-power-reads.md, module 9): the granted action prices above the item's
    /// rarity ceiling — the lane's <c>GrantedActionOverBudget</c>.</summary>
    public const string OverBudget = "grant.over-budget";

    /// <summary>⭐ R2's dominance answer, ENFORCED rather than reported: an action with no resolvable
    /// rung is <c>unpriced</c> and refused, never read as <c>0</c>. "Pricing it at zero would make
    /// every action-granting item strictly dominant" (§10.6).</summary>
    public const string Unpriced = "grant.unpriced";

    /// <summary>⛔ Recorded, not raised: X3 has not landed, so no <c>rpg_action</c> row exists for a
    /// grant to name. The rule id exists so the gap has a name a report can carry.
    /// See <see cref="ItemGrantLandedFlags.ActionCorpusProducerLanded"/>.</summary>
    public const string ActionCorpusAbsent = "grant.action-corpus-absent";

    /// <summary>⛔ Recorded, never raised: §3.7(d)'s <c>TooManyGrantedActions</c>. The cap question is
    /// CLOSED and the answer is "uncapped by design" —
    /// see <see cref="ItemGrantLimits.GrantedCountCapExists"/>.</summary>
    public const string TooManyGranted = "grant.too-many-granted";

    static ItemGrantRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Forces the static constructor from a call site that has no other reason to touch it.</summary>
    public static void EnsureRegistered() { }

    internal static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// ssot-granted-actions.md §5.2 — the whole item side of the seam, six columns, keyed on the BASE
/// TYPE's container id (§4.4), never on a rolled instance.
///
/// <para>⛔ <b>The Never list (§5.3) is the load-bearing half of this record.</b> There is no cooldown,
/// no cost, no target, no condition, no charge count and no override of any of them, and there never
/// will be: "if a column would let two items naming the same <c>action_id</c> behave differently, it
/// belongs to the action layer or it does not exist."</para>
/// </summary>
/// <param name="ContainerId">The base type's container id. FK → <c>item_base_type(container_id)</c>
/// once that table exists; checked against the loaded base-type facts until then.</param>
/// <param name="Seq">Stable authoring and display order. PK is <c>(container_id, seq)</c>.</param>
/// <param name="ActionId">FK → <c>rpg_action(action_id)</c>. <b>This is the entire seam.</b></param>
/// <param name="Role"><c>default-attack</c> or <c>granted</c>.</param>
/// <param name="Enabled">Content is disabled, never deleted (definitions §6).</param>
/// <param name="Revision">Joins the E8 content hash.</param>
public sealed record ItemGrantedActionRow(
    string ContainerId,
    int Seq,
    string ActionId,
    ItemGrantRole Role,
    bool Enabled = true,
    int Revision = 0)
{
    public string RoleWire => ItemGrantRoles.Wire(Role);
}

/// <summary>The <c>item.</c> container-id grammar a base type's id must satisfy — the same expression
/// <c>UniqueContainerIds</c> and <c>ConsumableContainerIds</c> use, so the three cannot drift.</summary>
public static class ItemGrantContainerIds
{
    public const string Prefix = ItemGrantLimits.ContainerIdPrefix + ".";

    static readonly Regex SlugRe = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static bool IsWellFormed(string? containerId) =>
        containerId is not null &&
        containerId.StartsWith(Prefix, StringComparison.Ordinal) &&
        SlugRe.IsMatch(containerId[Prefix.Length..]);
}
