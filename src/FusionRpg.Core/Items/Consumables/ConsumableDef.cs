using System.Text.RegularExpressions;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Consumables;

/// <summary>
/// ssot-consumables.md §3.1's six classes. <b>Closed, and it is code rather than data</b> because each
/// member names an <i>executor</i> — "adding a `restore` row ships a new potion with no new code; adding
/// a seventh class does not" (§5.2). v1 authors three; the other three are declared and ungenerated,
/// the same disposition D14 gave <c>standard</c>.
/// </summary>
public enum ConsumableClass
{
    /// <summary>Refills a pool now. Fires once at use; no lifetime.</summary>
    Restore = 0,

    /// <summary>A stat buff for the coming run. Applies at run start with the squad snapshot,
    /// withdrawn at run end — a lifetime expressed as a lifecycle, never a ms clock (§4.5).</summary>
    Draught,

    /// <summary>A depleting absorption layer, applied at battle setup. The only class with a real
    /// clock in v1: <c>BattleInnateShield.DurationMs</c>, integer ms at the content boundary.</summary>
    Ward,

    /// <summary>Something thrown at the lawn. <b>Declare-only</b> — blocked on an overlay use
    /// affordance and on <c>capPerMatch</c> (G4), which has no implementation anywhere.</summary>
    Board,

    /// <summary>Returns a <c>Downed</c> actor to the fight. <b>Declare-only</b> — the target state
    /// already exists (<c>TurnState</c>'s <c>Downed → Charging</c>); the missing half is the
    /// battle-mode use moment, which is the action layer's.</summary>
    Revive,

    /// <summary>A non-combat state change at a menu. <b>Declare-only</b> — no menu executor exists.</summary>
    Utility,
}

/// <summary>
/// ssot-consumables.md §5.2's closed <c>use_context</c> set, comma-joined on the wire. v1 authors
/// <c>menu</c>, <c>dispatch</c> and — since 2026-09-05 — <c>battle</c>; widening is additive and never
/// invalidates a row, which is the whole no-migration proof (§4.1).
/// </summary>
public enum UseContext
{
    /// <summary>Spent at a menu, out of combat.</summary>
    Menu = 0,

    /// <summary>Named in the pre-dispatch draught manifest; an input to the sealed run.</summary>
    Dispatch,

    /// <summary>Used mid-battle, through an action gated on <c>LeafId.HoldsStock</c>. <b>Authored
    /// since 2026-09-05</b>: the leaf reads the precondition (T10) and
    /// <c>IStockLedger</c>/<c>RpgStore.TrySpendStock</c> take the stack at commit. §6.2 code 4's
    /// "battle before the action layer" no longer holds.</summary>
    Battle,

    /// <summary>Used on the lawn through the intent/command road. ⛔ Still refused, and for two
    /// independent reasons: spec-usability-conditions.md §3a's mode matrix makes a <c>holdsStock</c>
    /// action NOT BINDABLE in lawn mode (the overlay is a stateless observer and never reads current
    /// inventory — <c>ActionCompiler</c> refuses it by name), and <c>capPerMatch</c> (G4) is
    /// unimplemented.</summary>
    Lawn,

    /// <summary>D3.24 (spec-supplies-and-objects.md §2) — used at a delve `rest` room, one of
    /// `rest.activations` per member. Appended after <see cref="Lawn"/>, never inserted earlier, so
    /// declaration order (which <c>UseContextWire</c> sorts by) keeps every existing wire string
    /// byte-identical. Wire spelling <c>"rest"</c> — the approved seed-contract spelling, not the
    /// brief's <c>"Rest"</c>/mismatched-case guess.</summary>
    Rest,

    /// <summary>D3.24 (spec-supplies-and-objects.md §2) — used at a room object between fights ("Room"
    /// in the brief; the approved seed contract and the map spell it <c>curio</c>, which wins). Same
    /// append-only placement as <see cref="Rest"/>.</summary>
    Curio,
}

/// <summary>The closed six, and the wire spellings <c>consumable_def.class_id</c> carries.</summary>
public static class ConsumableClasses
{
    public static readonly IReadOnlyList<ConsumableClass> All = new[]
    {
        ConsumableClass.Restore, ConsumableClass.Draught, ConsumableClass.Ward,
        ConsumableClass.Board, ConsumableClass.Revive, ConsumableClass.Utility,
    };

    public static string Wire(ConsumableClass c) => c switch
    {
        ConsumableClass.Restore => "restore",
        ConsumableClass.Draught => "draught",
        ConsumableClass.Ward => "ward",
        ConsumableClass.Board => "board",
        ConsumableClass.Revive => "revive",
        ConsumableClass.Utility => "utility",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    public static bool TryParse(string? id, out ConsumableClass c)
    {
        switch (id)
        {
            case "restore": c = ConsumableClass.Restore; return true;
            case "draught": c = ConsumableClass.Draught; return true;
            case "ward": c = ConsumableClass.Ward; return true;
            case "board": c = ConsumableClass.Board; return true;
            case "revive": c = ConsumableClass.Revive; return true;
            case "utility": c = ConsumableClass.Utility; return true;
            default: c = default; return false;
        }
    }
}

/// <summary>The closed six (D3.24 widened four to six — additive, "never invalidates a row"), their
/// wire spellings, and the runtime each one requires.</summary>
public static class UseContexts
{
    public static readonly IReadOnlyList<UseContext> All = new[]
    {
        UseContext.Menu, UseContext.Dispatch, UseContext.Battle, UseContext.Lawn, UseContext.Rest, UseContext.Curio,
    };

    public static string Wire(UseContext u) => u switch
    {
        UseContext.Menu => "menu",
        UseContext.Dispatch => "dispatch",
        UseContext.Battle => "battle",
        UseContext.Lawn => "lawn",
        UseContext.Rest => "rest",
        UseContext.Curio => "curio",
        _ => throw new ArgumentOutOfRangeException(nameof(u)),
    };

    public static bool TryParse(string? id, out UseContext u)
    {
        switch (id)
        {
            case "menu": u = UseContext.Menu; return true;
            case "dispatch": u = UseContext.Dispatch; return true;
            case "battle": u = UseContext.Battle; return true;
            case "lawn": u = UseContext.Lawn; return true;
            case "rest": u = UseContext.Rest; return true;
            case "curio": u = UseContext.Curio; return true;
            default: u = default; return false;
        }
    }

    /// <summary>
    /// ⭐ <b>A decision the spec does not state, made here and derived from the lane's own §6.2.</b>
    /// §6.3 requires "every atom in the core is legal in EVERY runtime named by <c>use_context</c>" —
    /// failure mode 5, the invisible nerf — but neither document says which
    /// <see cref="RuntimeId"/> each context names, and the four contexts do not map one-to-one onto
    /// the three runtimes.
    ///
    /// <list type="bullet">
    /// <item><c>battle</c> → <see cref="RuntimeId.Battle"/> and <c>lawn</c> → <see cref="RuntimeId.Lawn"/>
    /// are direct.</item>
    /// <item><c>dispatch</c> → <see cref="RuntimeId.Battle"/>: an expedition's encounters resolve
    /// through <c>BattleEngine</c>, and §5.4's projection lands on <c>BattleActorSetup</c>. A draught
    /// whose atom cannot execute in battle is exactly the silent no-op the check exists to refuse.</item>
    /// <item><c>menu</c> → <b>no combat runtime at all</b>. §6.2's own code-4 row names the two contexts
    /// a host may fail to serve — "<c>battle</c> before the action layer, <c>lawn</c> with no injector" —
    /// and <c>menu</c> is neither, so a menu consumable must NOT require the game to be running (SC8).
    /// ⛔ The honest consequence, named rather than hidden: <b>no menu executor exists</b>, so the check
    /// is vacuously true for the 26 menu-only rows in the shipped corpus. That is a wiring gap, and it
    /// is recorded as one — see <see cref="ConsumableRules.MenuExecutorAbsent"/>.</item>
    /// </list>
    ///
    /// ⚠ The lane's §7.1 "what runs today" cites the LAWN's grant path for a <c>menu</c> consumable,
    /// which contradicts its own §6.2 (the lawn is the context that needs the injector). §6.2 wins:
    /// it is the normative table, §7.1 is a worked example.
    /// </summary>
    public static IReadOnlyList<RuntimeId> RuntimesFor(UseContext u) => u switch
    {
        UseContext.Menu => Array.Empty<RuntimeId>(),
        UseContext.Dispatch => new[] { RuntimeId.Battle },
        UseContext.Battle => new[] { RuntimeId.Battle },
        UseContext.Lawn => new[] { RuntimeId.Lawn },
        // D3.24 (spec-supplies-and-objects.md §2, table row 2): "neither is a combat runtime; a supply
        // used at rest or at a curio resolves through the battle runtime's OnActivate grant path
        // OUTSIDE a fight, exactly as Dispatch maps to RuntimeId.Battle for the same reason" -- the
        // Menu -> [] precedent immediately above is the shape this follows.
        UseContext.Rest => Array.Empty<RuntimeId>(),
        UseContext.Curio => Array.Empty<RuntimeId>(),
        _ => throw new ArgumentOutOfRangeException(nameof(u)),
    };
}

/// <summary>
/// Structural limits of the consumable class. Every one is <b>exempt</b> from AGENTS.md's
/// no-hard-ceilings rule and, as that rule requires, says here why.
/// </summary>
public static class ConsumableLimits
{
    /// <summary>
    /// <b>STRUCTURAL — the atom layer's five tiers, not a dial.</b> <c>atom_id</c> derives as
    /// <c>{family}[.{variant}].t{tier}</c> for exactly five tiers (definitions.md §1) and
    /// `bands.v1.json` prices exactly five <c>powerBand</c>s against them. A sixth grade would need a
    /// sixth <c>.t6</c> row on every family — an atom-layer change, not a balance edit.
    /// </summary>
    public const int MinGrade = 1;

    /// <summary>See <see cref="MinGrade"/>.</summary>
    public const int MaxGrade = 5;

    /// <summary>
    /// <b>STRUCTURAL, and it is the reason the belt limit is a limit.</b> A consumable occupying zero
    /// manifest places is free, so any number of them fit in any belt — the carry rule would refuse
    /// nothing. This is a floor, not a ceiling: <c>manifest_cost</c> has no upper bound here, because
    /// a strong draught costing several places is exactly what the column is for (§5.2).
    /// </summary>
    public const int MinManifestCost = 1;

    /// <summary>
    /// ⭐ <b>D37, and STRUCTURAL rather than a default.</b> With no <c>girdle</c> equipped the carry
    /// count is <b>0</b> — "an unequipped slot grants nothing, exactly as every other role behaves."
    /// Not a progression ceiling: the limit itself is the equipped belt's own <c>consumableSlots</c>,
    /// which is CONTENT on a base type and grows by playing. A non-zero default here would be a global
    /// carry limit no item earned, which is precisely what D37 withdrew.
    /// </summary>
    public const int UnbeltedSlots = 0;

    /// <summary>
    /// ⭐ <b>X7 landed (`container-kind-expansion`, 2026-09-07)</b> — <see cref="ContainerKind"/> now
    /// ships <see cref="ContainerKind.Consumable"/>, so a consumable's own container kind is real
    /// rather than the documented `item`-with-`slot IS NULL` fallback spec-consumables.md's §Open
    /// once reserved for the owner. `ConsumableValidator.ValidateDef` now checks the bound kind IS
    /// specifically <see cref="ContainerKind.Consumable"/> (see
    /// <see cref="ConsumableRules.ContainerKindUnavailable"/>'s corrected doc) rather than refusing
    /// every kind unconditionally.
    /// </summary>
    public const bool ConsumableContainerKindAvailable = true;

    /// <summary>The <c>container_id</c> prefix §4.6 fixes for the kind, once it exists.</summary>
    public const string ContainerIdPrefix = "consumable";
}

/// <summary>
/// This module's content-rule namespace. item-ideal.md §2b.1 and README #3: <b>one</b>
/// <see cref="AtomRejectionReason.ContentRuleViolated"/> code carrying a namespaced rule id, never a
/// new member of the closed enum.
///
/// <para>⛔ ssot-consumables.md §6.2 proposes <b>four new codes</b> — <c>ConsumableRolls</c>,
/// <c>DraughtLimitExceeded</c>, <c>DraughtFamilyConflict</c>, <c>UseContextUnsupported</c>. This module
/// mints <b>none</b>. The closed list stays at 35 and each of those four is a <c>consumable.*</c> rule
/// id below, keeping the player-visible distinction the lane argued for without growing a vocabulary
/// no operator can hold. Module 11 and module 17 made the same call for their own proposed codes.</para>
/// </summary>
public static class ConsumableRules
{
    public const string Namespace = "consumable";

    /// <summary>§6.2 code 1 (<c>ConsumableRolls</c>): a consumable container declares rolls, a tier
    /// window, or a rarity. A consumable does not roll, so it has no rarity in the sense this tree
    /// uses the word.</summary>
    public const string Rolls = "consumable.rolls";

    /// <summary>§6.2 code 2 (<c>DraughtLimitExceeded</c>): the manifest's summed <c>manifest_cost</c>
    /// exceeds the equipped belt's <c>consumableSlots</c>.</summary>
    public const string LimitExceeded = "consumable.limit-exceeded";

    /// <summary>§6.2 code 3 (<c>DraughtFamilyConflict</c>): two manifest entries share an
    /// <c>exclusion_group</c>.</summary>
    public const string FamilyConflict = "consumable.family-conflict";

    /// <summary>§6.2 code 4 (<c>UseContextUnsupported</c>): used in a context its <c>use_context</c>
    /// does not name, or in one the host cannot serve.</summary>
    public const string UseContextUnsupported = "consumable.use-context-unsupported";

    /// <summary>A class the closed enum declares but v1 does not author, because it has no executor.</summary>
    public const string ClassUnavailable = "consumable.class-unavailable";

    /// <summary>The lifecycle path honours neither <c>chance</c> nor <c>icd_ms</c>
    /// (<c>EffectBag.FireGrant</c> short-circuits both), so a consumable may author neither.</summary>
    public const string ParamNotHonoured = "consumable.param-not-honoured";

    /// <summary>An atom whose kind is unsupported in a runtime the <c>use_context</c> names —
    /// failure mode 5, the invisible nerf, refused at catalog load rather than discovered in play.</summary>
    public const string RuntimeUnsupported = "consumable.runtime-unsupported";

    /// <summary><c>grade</c> does not equal the tier of every core atom (I3's band-consistency rule).</summary>
    public const string GradeMismatch = "consumable.grade-mismatch";

    /// <summary>An atom naming a trigger its kind does not carry.</summary>
    public const string TriggerNotAllowed = "consumable.trigger-not-allowed";

    /// <summary>A <c>consumable</c> container with no <c>consumable_def</c> row, or the reverse.
    /// An orphan container is not usable content.</summary>
    public const string Orphan = "consumable.orphan";

    /// <summary>The manifest names a container the catalog does not know.</summary>
    public const string UnknownConsumable = "consumable.unknown";

    /// <summary>A malformed value: <c>grade</c> outside 1..5, <c>manifest_cost</c> below 1,
    /// <c>qty</c> at or below zero.</summary>
    public const string BadValue = "consumable.bad-value";

    /// <summary>A consumable's declared container kind is not <see cref="ContainerKind.Consumable"/>
    /// (X7 landed 2026-09-07 — see <see cref="ConsumableLimits.ConsumableContainerKindAvailable"/>);
    /// binding a `consumable_def` to any other kind is a real, catchable content mistake now, not
    /// the universal "nothing can bind yet" refusal this rule used to mean.</summary>
    public const string ContainerKindUnavailable = "consumable.container-kind-unavailable";

    /// <summary>⛔ Recorded, not raised: there is no out-of-combat executor for a <c>menu</c>
    /// consumable. The rule id exists so the gap has a name a report can carry; nothing refuses on it,
    /// because refusing 26 authored rows for a missing executor would be refusing the corpus for the
    /// runtime's gap.</summary>
    public const string MenuExecutorAbsent = "consumable.menu-executor-absent";

    /// <summary>The corpus file itself is not the shape the seed contract fixes.</summary>
    public const string CorpusMalformed = "consumable.corpus-malformed";

    static ConsumableRules() => ContentRuleNamespaces.Register(Namespace);

    /// <summary>Forces the static constructor from a call site that has no other reason to touch it.</summary>
    public static void EnsureRegistered() { }

    internal static AtomRejection Fail(string ruleId, string detail)
    {
        EnsureRegistered();
        return AtomRejection.ContentRule(ruleId, detail);
    }
}

/// <summary>
/// The <c>consumable_def</c> row — ssot-consumables.md §5.2's nine columns, 1:1 on an
/// <c>effect_container</c>.
/// </summary>
/// <param name="ContainerId">
/// <c>consumable.{slug}</c>. ⭐ <b>Unlike a unique's, the corpus's own tracking id IS already a legal
/// container id</b> — `naming.v1.json`'s template is <c>consumable.k{slot}-{seq:03}</c> and §4.6 fixes
/// the kind's prefix as <c>consumable.</c>, so the two coincide and no derivation is needed. Only a
/// grammar check is (<see cref="ConsumableContainerIds"/>).
/// </param>
/// <param name="UseContexts">
/// Comma-joined on the wire, in <see cref="UseContext"/> declaration order so two logs of one row are
/// byte-comparable. Never empty: a consumable usable nowhere is not content.
/// </param>
/// <param name="Grade">1..5, the strength axis. Must equal the tier of every core atom.</param>
/// <param name="ExclusionGroup">
/// The one-per-run key. Defaults to the container's dominant <c>(family_id, variant)</c> — the shipped
/// <c>ContainerPoolRow.Group</c> default, reused rather than reinvented.
/// </param>
/// <param name="ManifestCost">How many of the belt's places this consumable occupies.</param>
/// <param name="GrantsActionId">⏸ The seam to the action layer. <c>NULL</c> means menu/dispatch only.</param>
/// <param name="CooldownKey">⏸ Reserved for <c>rpg_action.cooldown_key</c>. Inert in v1, authored now
/// because a cooldown group retrofitted after content ships re-prices every row that already shipped.</param>
public sealed record ConsumableDefRow(
    string ContainerId,
    ConsumableClass ClassId,
    IReadOnlyList<UseContext> UseContexts,
    int Grade,
    string ExclusionGroup,
    int ManifestCost = 1,
    string? GrantsActionId = null,
    string? CooldownKey = null,
    bool Enabled = true,
    int Revision = 1)
{
    /// <summary>The wire form of <see cref="UseContexts"/> — comma-joined, in declaration order.</summary>
    public string UseContextWire =>
        string.Join(",", Consumables.UseContexts.All.Where(UseContexts.Contains).Select(Consumables.UseContexts.Wire));
}

/// <summary>
/// The <c>consumable.</c> container-id grammar. There is no seed→container derivation here (see
/// <see cref="ConsumableDefRow.ContainerId"/>) — only the check that the corpus's id is already one.
/// </summary>
public static class ConsumableContainerIds
{
    public const string Prefix = ConsumableLimits.ContainerIdPrefix + ".";

    /// <summary>The container-id body grammar `definitions.md` §1 fixes, mirrored here — the same
    /// expression <c>UniqueContainerIds</c> uses, so the two cannot drift.</summary>
    static readonly Regex SlugRe = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    public static bool IsWellFormed(string? containerId) =>
        containerId is not null &&
        containerId.StartsWith(Prefix, StringComparison.Ordinal) &&
        SlugRe.IsMatch(containerId[Prefix.Length..]);
}
