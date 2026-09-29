using System.Text.RegularExpressions;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>
/// The `charm` content-rule namespace, and the rule ids the CARRY layer raises at import.
///
/// <para><b>⛔ No new member of the closed 33-code list.</b> ssot-charms.md §5.2 proposed five new
/// codes (<c>CharmBudgetExceeded</c>, <c>CharmAxisOverflow</c>, <c>CharmInUse</c>,
/// <c>CharmNotCarryable</c>, <c>CharmAtomNotPermitted</c>) and said outright that five is a large ask.
/// This module mints none of them, because the program has since answered the question twice:</para>
///
/// <list type="bullet">
/// <item>an <b>authoring</b> failure is a namespaced <see cref="AtomRejectionReason.ContentRuleViolated"/>
/// under this prefix — the device modules 1, 7, 11, 12, 17 and 18 all use;</item>
/// <item>a <b>player-action</b> refusal is a module-local reason enum
/// (<see cref="CharmCarryRefusalReason"/>) — the device module 4 used for <c>EquipRefusalReason</c>,
/// because "may this player attune this charm?" is not an atom rejection at all.</item>
/// </list>
///
/// <para>The five names survive verbatim as the enum's members and these rule ids, so a UI string or a
/// support question can still be looked up by the name the lane doc uses.</para>
/// </summary>
public static class CharmCarryRules
{
    public const string Namespace = "charm";

    static CharmCarryRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Force the static constructor, so the `charm` namespace is registered before use.</summary>
    public static void EnsureRegistered() => System.Runtime.CompilerServices.RuntimeHelpers
        .RunClassConstructor(typeof(CharmCarryRules).TypeHandle);

    /// <summary>§5.2 code 5 (<c>CharmAtomNotPermitted</c>): a `charm.` container holds an atom charms
    /// may not carry — <c>op = Increased</c>/<c>More</c> (§3.4, "flat only"), or a board/grid/box/spawn
    /// kind. An <b>authoring</b> error, caught at import, never at attunement.</summary>
    public const string AtomNotPermitted = "charm.atom-not-permitted";

    /// <summary>§3.7: a charm whose atoms are all frame-restricted must declare the matching
    /// <c>frame_hint</c>, "or the player carries a dead charm and never learns why". A mismatch is a
    /// rejection, not a warning.</summary>
    public const string FrameHintMismatch = "charm.frame-hint-mismatch";

    /// <summary>An <c>ap_cost</c> outside the tuning's authored domain. §3.3 — the domain is
    /// <c>{1,2,3,5}</c> today and the gap between 3 and 5 is the mechanic.</summary>
    public const string ApCostOutsideDomain = "charm.ap-cost-outside-domain";

    /// <summary>A resonance container carrying a <c>charm_def</c> row. §4.2: "a `charm.` container with
    /// no <c>charm_def</c> row is not attunable — that is how resonance containers stay out of the
    /// pouch", so authoring one a def IS the authoring error.</summary>
    public const string ResonanceIsAttunable = "charm.resonance-is-attunable";

    public static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// Why the pouch gate refused one player action. <b>Module-local, like module 4's
/// <c>EquipRefusalReason</c></b> — not a member of definitions.md §10's closed 33, and not a
/// request to open it. The names are ssot-charms.md §5.2's own, kept verbatim.
/// </summary>
public enum CharmCarryRefusalReason
{
    /// <summary>§5.2 code 1. The snapshot's summed <c>ap_cost</c> exceeds the player's capacity.
    /// The mechanic's primary refusal, and the one a player sees constantly.</summary>
    CharmBudgetExceeded,

    /// <summary>§5.2 code 2. More than <c>axisCapPerSnapshot</c> charms of one axis. A DIFFERENT
    /// mistake from a budget overflow, with a different fix — drop <i>this</i> charm, not any charm —
    /// which is exactly why the lane refused the fold-to-three variant's cost.</summary>
    CharmAxisOverflow,

    /// <summary>§5.2 code 3. Un-attuning, or attuning elsewhere, a charm a live run holds. The content
    /// is fine and the <i>player</i> is elsewhere, which is why <c>StaleInstance</c> would lie.</summary>
    CharmInUse,

    /// <summary>§5.2 code 4. The instance's container is not a charm, or is a resonance container
    /// (no <c>charm_def</c> row). The container resolves fine, so <c>UnknownContainer</c> would lie.</summary>
    CharmNotCarryable,

    /// <summary>Reused code (§5.1): more copies of one <c>container_id</c> than its copy cap allows.
    /// Two limits, one gate — <c>unique_carry</c> is the tighter one, per <c>container_id</c>.</summary>
    DuplicateKey,

    /// <summary>Reused code (§5.1): <c>level_req</c> set and the PLAYER's level is lower.</summary>
    LevelTooLow,

    /// <summary>
    /// ⛔ Not in §5.1 or §5.2, and it is here because §8 item 6 is <b>still unanswered</b>: <c>players</c>
    /// is <c>(id, name, created_utc, world_seed)</c> — <b>there is no player level to compare against.</b>
    /// A charm that declares a <c>level_req</c> against a caller that cannot supply one is a check the
    /// gate cannot make, and SC6 says reject rather than ignore. Inert on today's corpus: no shipped
    /// charm authors a <c>levelReq</c>, so this can only fire once one does.
    /// </summary>
    PlayerLevelUnavailable,
}

/// <summary>One refusal, naming the charm the player must act on. Empty ids for a whole-pouch rule.</summary>
public readonly record struct CharmCarryRefusal(
    CharmCarryRefusalReason Reason, string InstanceId, string ContainerId, string Detail);

/// <summary>
/// One attuned charm as the gate sees it — a marking on a row I13 already holds, never a copy of it.
/// </summary>
/// <param name="ApCost">
/// <b><c>long</c>, because the gate SUMS it</b> against a capacity (AGENTS.md: <c>long</c> for any
/// magnitude). Authored on the base type and never rolled (§3.3).
/// </param>
/// <param name="LevelReq">
/// From <c>effect_container.level_req</c>, checked against the <b>player's</b> level, not a specimen's.
/// <c>null</c> on every shipped charm today.
/// </param>
public readonly record struct AttunedCharm(
    string InstanceId,
    string ContainerId,
    string Axis,
    long ApCost,
    bool UniqueCarry,
    int? LevelReq = null);

/// <summary>
/// ssot-charms.md §5 — the carry gate. <b>It runs at attunement and AGAIN over the snapshot at run
/// start.</b> Both reject; neither ignores.
///
/// <para>Re-checking at run start is not redundancy for its own sake (§5.3): capacity can <i>shrink</i>
/// (a respec), a container can be disabled between attunement and dispatch, and a snapshot that binds
/// under a stale gate is exactly the drift that produces an un-reproducible run.</para>
///
/// <para><b>Returns EVERY refusal rather than first-fail</b> — module 17's rule, kept: a pouch reported
/// one problem at a time is one round trip per mistake, and the player is holding all of them at once.</para>
///
/// <para><b>⛔ Nothing here clamps.</b> <paramref name="capacityAp"/> is whatever the caller passes,
/// including a value above every rung of <see cref="CharmAttunementTuning.CapacityLadder"/>: AGENTS.md
/// forbids a hard progression ceiling, and the ladder is the last AUTHORED rung, not a maximum. An AP
/// sum is <c>checked</c>, so an overflow throws and never wraps.</para>
/// </summary>
public static class CharmPouchGate
{
    /// <summary>
    /// Mirrors <see cref="ThresholdContainerIds.CharmResonance"/>'s grammar, and deliberately accepts
    /// the UNPADDED spelling the corpus ships (<c>charm.res-offense-2</c>) as well as the canonical
    /// padded one — module 12 measured that divergence rather than normalising it away, and a gate that
    /// only recognised the canonical form would let all ten shipped resonance containers into the pouch.
    /// </summary>
    static readonly Regex ResonanceIdRe =
        new("^charm\\.res-[a-z0-9]+(?:-[a-z0-9]+)*-[0-9]{1,2}$", RegexOptions.Compiled);

    /// <summary>§4.2's "that is how resonance containers stay out of the pouch", as a predicate.</summary>
    public static bool IsResonanceContainer(string containerId) =>
        containerId is not null && ResonanceIdRe.IsMatch(containerId);

    /// <summary>
    /// The snapshot's total AP. <c>checked</c> and <c>long</c>: overflow throws rather than wrapping a
    /// budget into a pouch that fits everything.
    /// </summary>
    public static long TotalAp(IEnumerable<AttunedCharm> snapshot)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        long total = 0;
        checked { foreach (var c in snapshot) total += c.ApCost; }
        return total;
    }

    /// <summary>True when the whole pouch passes every rule.</summary>
    public static bool Admits(
        IReadOnlyList<AttunedCharm> snapshot,
        long capacityAp,
        CharmAttunementTuning tuning,
        int? playerLevel = null,
        IReadOnlySet<string>? attunableContainerIds = null,
        IReadOnlyDictionary<string, string>? heldByOtherRun = null)
        => Explain(snapshot, capacityAp, tuning, playerLevel, attunableContainerIds, heldByOtherRun).Count == 0;

    /// <summary>
    /// Every rule §5.3 runs at attune-time and again at run start, in one pass.
    /// </summary>
    /// <param name="attunableContainerIds">
    /// The <c>charm_def</c> keys — §4.2's "a `charm.` container with no <c>charm_def</c> row is not
    /// attunable". <c>null</c> means the caller has no def table to hand and only the resonance-id shape
    /// is checked; a caller that HAS one always passes it, because that is the stronger check.
    /// </param>
    /// <param name="heldByOtherRun">
    /// <c>instance_id</c> → a label for the run holding it. Supplied by the DAL from
    /// <c>charm_run_hold</c> WHERE <c>active = 1</c>; the partial unique index is what makes the rule
    /// structural, and this is the same fact read early so the UI can name the run (§7.5).
    /// </param>
    public static IReadOnlyList<CharmCarryRefusal> Explain(
        IReadOnlyList<AttunedCharm> snapshot,
        long capacityAp,
        CharmAttunementTuning tuning,
        int? playerLevel = null,
        IReadOnlySet<string>? attunableContainerIds = null,
        IReadOnlyDictionary<string, string>? heldByOtherRun = null)
    {
        if (snapshot is null) throw new ArgumentNullException(nameof(snapshot));
        if (capacityAp < 0)
            throw new ArgumentOutOfRangeException(nameof(capacityAp),
                $"capacity {capacityAp} is negative; §5.1 maps that to BadParamValue at the write, and " +
                "the gate must never be handed one");

        var fails = new List<CharmCarryRefusal>();

        // ---- carryability (§5.2 code 4) ------------------------------------------------------------
        foreach (var c in snapshot)
        {
            if (IsResonanceContainer(c.ContainerId))
            {
                fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.CharmNotCarryable,
                    c.InstanceId, c.ContainerId,
                    "a resonance container is granted BY the pouch and can never sit in it (§4.2) — it " +
                    "carries no charm_def row on purpose"));
                continue;
            }

            if (attunableContainerIds is not null && !attunableContainerIds.Contains(c.ContainerId))
                fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.CharmNotCarryable,
                    c.InstanceId, c.ContainerId,
                    "no charm_def row — §4.2 makes the def table the attunable list, so a container " +
                    "without one resolves fine and is still not a charm you may carry"));
        }

        // ---- the budget (§5.2 code 1) --------------------------------------------------------------
        var total = TotalAp(snapshot);
        if (total > capacityAp)
            fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.CharmBudgetExceeded, "", "",
                $"the pouch costs {total} AP against a capacity of {capacityAp}"));

        // ---- the axis cap (§5.2 code 2) ------------------------------------------------------------
        var perAxis = new Dictionary<string, List<AttunedCharm>>(StringComparer.Ordinal);
        foreach (var c in snapshot)
        {
            if (!perAxis.TryGetValue(c.Axis, out var list)) perAxis[c.Axis] = list = new List<AttunedCharm>();
            list.Add(c);
        }

        foreach (var axis in perAxis.Keys.OrderBy(a => a, StringComparer.Ordinal))
        {
            var members = perAxis[axis];
            if (members.Count <= tuning.AxisCapPerSnapshot) continue;

            // Name the charms past the cap, in snapshot order: "drop THIS charm, not any charm" is the
            // whole reason this is a separate refusal from the budget one.
            foreach (var over in members.Skip(tuning.AxisCapPerSnapshot))
                fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.CharmAxisOverflow,
                    over.InstanceId, over.ContainerId,
                    $"axis '{axis}' already holds {tuning.AxisCapPerSnapshot} charms, the cap; a fourth " +
                    "contributing nothing would be a silent no-op, so it refuses instead (§3.3)"));
        }

        // ---- the copy cap (§5.1, reusing DuplicateKey) ---------------------------------------------
        var perContainer = new Dictionary<string, List<AttunedCharm>>(StringComparer.Ordinal);
        foreach (var c in snapshot)
        {
            if (!perContainer.TryGetValue(c.ContainerId, out var list))
                perContainer[c.ContainerId] = list = new List<AttunedCharm>();
            list.Add(c);
        }

        foreach (var containerId in perContainer.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var copies = perContainer[containerId];
            // The tighter of the two limits wins, per container_id. `unique_carry` is authored on the
            // base type, so any one copy declaring it settles it for all of them.
            var cap = tuning.CopyCapFor(copies.Any(c => c.UniqueCarry));
            if (copies.Count <= cap) continue;

            foreach (var over in copies.Skip(cap))
                fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.DuplicateKey,
                    over.InstanceId, containerId,
                    $"the pouch holds {copies.Count} copies of '{containerId}' and the cap is {cap}" +
                    (cap == tuning.UniqueCarryCopyCap && cap != tuning.CopyCapPerContainer
                        ? " — this charm is unique_carry, which is tighter than the default"
                        : "")));
        }

        // ---- level_req (§5.1) and the missing player level -----------------------------------------
        foreach (var c in snapshot)
        {
            if (c.LevelReq is not { } req) continue;

            if (playerLevel is null)
            {
                fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.PlayerLevelUnavailable,
                    c.InstanceId, c.ContainerId,
                    $"'{c.ContainerId}' requires player level {req} and no player level was supplied — " +
                    "ssot-charms.md §8 item 6 is unanswered and `players` carries no level column, so " +
                    "the gate refuses rather than passing a check it cannot make (SC6)"));
                continue;
            }

            if (playerLevel.Value < req)
                fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.LevelTooLow,
                    c.InstanceId, c.ContainerId,
                    $"needs player level {req}, the player is level {playerLevel.Value}"));
        }

        // ---- cross-run exclusivity (§5.2 code 3) ---------------------------------------------------
        if (heldByOtherRun is { Count: > 0 })
            foreach (var c in snapshot)
                if (heldByOtherRun.TryGetValue(c.InstanceId, out var run))
                    fails.Add(new CharmCarryRefusal(CharmCarryRefusalReason.CharmInUse,
                        c.InstanceId, c.ContainerId,
                        $"a live run ({run}) holds this charm; it is never silently held or silently " +
                        "dropped — the pouch UI names the run (§7.5)"));

        return fails;
    }

    /// <summary>
    /// The import-time authoring checks §5.3's first column runs, over one <see cref="CharmDef"/>.
    /// Every failure, never first-fail. These are <b>content</b> rules, so they are
    /// <see cref="AtomRejectionReason.ContentRuleViolated"/> under <see cref="CharmCarryRules"/>.
    ///
    /// <para><paramref name="atomFrames"/> is §3.7's check made real: the frames the container's atoms
    /// actually serve. ⚠ <b>Inert on today's corpus</b> — all 60 shipped charms declare
    /// <c>frameHint: any</c> and the atom rows carry no frame yet — and written anyway, because §3.7's
    /// whole point is that the FIRST frame-restricted charm must not ship as a silent dud.</para>
    /// </summary>
    public static IReadOnlyList<AtomRejection> ValidateForCarry(
        CharmDef def,
        CharmAttunementTuning tuning,
        string frameHint = "any",
        IReadOnlyCollection<string>? atomFrames = null)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        var fails = new List<AtomRejection>();

        if (IsResonanceContainer(def.ContainerId))
            fails.Add(CharmCarryRules.Fail(CharmCarryRules.ResonanceIsAttunable,
                $"'{def.ContainerId}' is a resonance container and carries a charm_def row; §4.2 keeps " +
                "resonance out of the pouch precisely by giving it no def"));

        if (!tuning.ApCostDomain.Contains(def.ApCost))
            fails.Add(CharmCarryRules.Fail(CharmCarryRules.ApCostOutsideDomain,
                $"'{def.ContainerId}' costs {def.ApCost} AP; the authored domain is " +
                $"[{string.Join(", ", tuning.ApCostDomain)}] (§3.3) and a size outside it makes the " +
                "packing decision unreadable"));

        // §3.7. `any` is always legal; a specific hint must be one the atoms actually serve, or the
        // player carries a dead charm and never learns why.
        if (!string.Equals(frameHint, "any", StringComparison.Ordinal))
        {
            if (frameHint is not ("humanoid" or "plant"))
                fails.Add(CharmCarryRules.Fail(CharmCarryRules.FrameHintMismatch,
                    $"'{def.ContainerId}' declares frame_hint '{frameHint}'; §3.7 has exactly three " +
                    "(any | humanoid | plant)"));
            else if (atomFrames is { Count: > 0 } && !atomFrames.Contains(frameHint))
                fails.Add(CharmCarryRules.Fail(CharmCarryRules.FrameHintMismatch,
                    $"'{def.ContainerId}' declares frame_hint '{frameHint}' but its atoms serve " +
                    $"[{string.Join(", ", atomFrames.OrderBy(f => f, StringComparer.Ordinal))}] — a " +
                    "mismatch is a rejection, not a warning (§3.7)"));
        }

        return fails;
    }
}
