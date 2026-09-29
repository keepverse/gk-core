using FusionRpg.Core.Battle.Timeline;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions;

/// <summary>
/// One row per action (spec-action-model.md §2). Timing and cooldown are the existing
/// <see cref="ActionEnvelope"/> — not a second copy — folded in as `A5`'s three envelope gaps land.
///
/// <para>Range (<see cref="MinRange"/>/<see cref="MaxRange"/>/<see cref="RangeChannel"/>) lives here,
/// not on <see cref="ActionTargetSpec"/> — spec-targeting.md: it gates whether the action may be
/// used at all (`A4`) as well as which targets qualify, so it is not a property of the target rule
/// alone.</para>
/// </summary>
public sealed record ActionRow
{
    // ---- identity --------------------------------------------------------------------------
    public string ActionId { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>
    /// item-content `granted-action-text` (T14): the display KEY for the action's player-facing
    /// description — never the sentence itself. `ssot-presentation.md` §3.6 L3: content-authored
    /// display text lives in a `_key` column and resolves through the string catalog
    /// (`gk-content/content/display/en.json`), the same rule `rarity.display_key` and `item_unique.flavour_key`
    /// already follow.
    ///
    /// <para>Empty means "this action has no authored description" — a real, visible absence that
    /// <c>DisplayRules.MissingDisplayKey</c> reports, never a synthesised sentence. Block 9 of the
    /// item card (`ssot-presentation.md` §9.14) is the reader.</para>
    /// </summary>
    public string DescriptionKey { get; init; } = "";

    public ActionKind Kind { get; init; } = ActionKind.Skill;

    /// <summary>Indexes `A12`'s rung table. Never a magnitude — the table holds the multipliers.</summary>
    public int Rung { get; init; }

    public IReadOnlyList<ActionTag> Tags { get; init; } = Array.Empty<ActionTag>();
    public bool Enabled { get; init; } = true;
    public long Revision { get; init; }

    // ---- grant -----------------------------------------------------------------------------
    public bool Grantable { get; init; }
    public bool DefaultAttackEligible { get; init; }

    // ---- effects ---------------------------------------------------------------------------
    public string ContainerId { get; init; } = "";

    // ---- timing / cooldown — the existing envelope, reused verbatim -----------------------
    public ActionEnvelope Envelope { get; init; } = ActionEnvelope.NoOp;

    // ---- targeting ---------------------------------------------------------------------------
    public ActionTargetSpec Targeting { get; init; } = new();

    /// <summary>With no board, every range check passes — not an error, not empty (spec-targeting.md §4).</summary>
    public int MinRange { get; init; }
    public int MaxRange { get; init; }

    /// <summary>Which derived channel range reads, mirroring `ActionEnvelope.SpeedChannel`'s shape.</summary>
    public string? RangeChannel { get; init; }

    public bool RequiresLineOfSight { get; init; }

    /// <summary>base-defense `siege-cover` decision 35: which shooting penalties (range, obstruction,
    /// melee lock) this action pays. Default `All` — an ordinary shot pays everything, and an
    /// exemption is authored content, never an oversight.</summary>
    public ProjectilePenalties ProjectilePenalties { get; init; } = ProjectilePenalties.All;

    // ---- usability -----------------------------------------------------------------------------
    /// <summary>Raw predicate JSON — compiled through E3, not here (A4).</summary>
    public string? ConditionsJson { get; init; }

    // ---- eligibility (A-E1, spec-eligibility-axis.md §3.1) -----------------------------------
    /// <summary>Which tier's rule decides who may hold this action. Column, not table — every
    /// action has exactly one scope (§3.1).</summary>
    public EligibilityScope Scope { get; init; } = EligibilityScope.General;

    /// <summary><c>null</c> for <see cref="EligibilityScope.General"/>; a family id for
    /// <see cref="EligibilityScope.Family"/>; a species key for <see cref="EligibilityScope.Species"/>.
    /// Opaque — never joined against the creature catalog (§4, matching <see cref="SpeciesBasicsRow"/>'s
    /// own discipline).</summary>
    public string? ScopeKey { get; init; }

    // ---- corpus metadata (A-E1 §3.0 — six fields the corpus needs and ActionRow had no home for) --
    /// <summary>Reuses the existing <see cref="ActionCategory"/> vocabulary (§4 — never a second one).
    /// <c>null</c> for an action the corpus has not categorized (every basic/innate authored before
    /// this module shipped).</summary>
    public ActionCategory? Category { get; init; }

    /// <summary>Whether this action sets up or cashes in an <c>EnablerPayoffPairings</c> pairing.
    /// <see cref="PairingRole.None"/> is the default — a real value, never absence.</summary>
    public PairingRole PairingRole { get; init; } = PairingRole.None;

    /// <summary>Structure-axis tags the corpus authored for this action (A-S3's fingerprint, A-S4's
    /// g2). Opaque strings — this module does not own or validate the axis vocabulary.</summary>
    public IReadOnlyList<string> StructureAxes { get; init; } = Array.Empty<string>();

    /// <summary>Atom-family ids this action's pool draws reference (E30's pool references). Opaque
    /// strings, drawn from <c>gk-data/packs/fusion/data/seed/items/affix-families/*.json</c> — this module does not
    /// validate membership (that is A-C1's job).</summary>
    public IReadOnlyList<string> AtomFamilies { get; init; } = Array.Empty<string>();

    /// <summary>The authored rung window. <see cref="Rung"/> stays the single collapsed value
    /// <see cref="StructureBudgetGuard.Check"/> actually resolves against
    /// (<see cref="RungBand.Collapse"/> — <c>Rung = rungBand[1]</c>, the ceiling, §3.0). <c>null</c>
    /// for an action authored before this module shipped, which carries only <see cref="Rung"/>.</summary>
    public RungBand? RungBand { get; init; }
}

/// <summary>A-E1 (spec-eligibility-axis.md §3.0, acceptance 1b): the authored `[floor, ceiling]`
/// window a corpus action's <see cref="ActionRow.Rung"/> collapses from. <see cref="Collapse"/> is the
/// one stated rule — <c>Rung = rungBand[1]</c>, the ceiling — because <see cref="StructureBudgetGuard"/>
/// resolves a single rung-table row and a band silently becoming its floor or its ceiling is a balance
/// decision an implementation detail must not make quietly.</summary>
public sealed record RungBand(int Floor, int Ceiling)
{
    public int Collapse() => Ceiling;
}

/// <summary>A-U1 (spec-rung-semantics.md §3.1, 2026-09-03): the holder-derived rung
/// (<c>Unlock.UnlockLadder.EffectiveRung</c> — <c>min(earnCount, rungCap)</c>), wrapped so it cannot
/// silently re-merge with the AUTHORED <see cref="ActionRow.Rung"/> int
/// <see cref="StructureBudgetGuard"/> reads — the exact confusion the spec's own §1 finding names
/// ("does a rung mean the same thing to the author, the holder and the guard? Today it does not").
/// Fixes magnitude and cost for one holder; never structure, which is a property of the content.</summary>
public readonly record struct EffectiveRung(int Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>
/// `(action_id, resource_id, amount_spec, when)` — spec-action-model.md §3. A table, not columns,
/// because one action may cost several resources.
/// </summary>
/// <param name="AllowLethal">aura-skill T14 (`resource-hub-ssot.md`): an `hp` cost floors at 1 by
/// default — `CostLedger` refuses (`CannotAfford("hp")`) rather than let payment bring an actor to 0
/// or below — unless the action explicitly opts into lethality here. Ignored for every other resource
/// id; those pools reaching 0 is an ordinary, legal state. Defaults to `false` so every existing
/// 4-argument call site (positional or named) is unaffected.</param>
public sealed record ActionCostRow(
    string ActionId, string ResourceId, ValueSpec AmountSpec, ActionCostTiming When, bool AllowLethal = false);

/// <summary>
/// `(action_id, atom_id, scope)` — spec-action-model.md §4. Rows are optional; an atom with no row
/// defaults to <see cref="ActionEffectScope.EachTarget"/>.
/// </summary>
public sealed record ActionScopeRow(string ActionId, string AtomId, ActionEffectScope Scope);

/// <summary>
/// `rpg_action_grant(owner_kind, owner_key, action_id, source, grant_role, ...)` — spec-action-model.md
/// §5. A NEW table, not `effect_binding`: a granted action has no instance and no rolls.
/// </summary>
public sealed record ActionGrantRow(
    OwnerKind OwnerKind, string OwnerKey, string ActionId, string Source, string GrantRole = "");

/// <summary>
/// The source of an actor's three basics plus its innate (spec-action-model.md §1, §9). A narrow,
/// action-program-owned table keyed on an opaque `species_key` the caller supplies — deliberately not
/// a join into the generated creature-species catalog, which this program does not own and must not edit.
/// </summary>
public sealed record SpeciesBasicsRow(
    string SpeciesKey, string AttackActionId, string GuardActionId, string MoveActionId, string? InnateActionId);
