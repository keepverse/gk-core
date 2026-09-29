using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Delve.Quests;

/// <summary>
/// D4.9 (spec-delve-quests.md §1; spec-dungeon-seed-contract.md §1.5) — one quest anchor
/// (`quests/&lt;id&gt;.json`): a template INSTANCE, never a new mechanism. `TargetRef` is a kind ref,
/// never an id or a number (spec, verbatim) — `null` for the templates whose own registry
/// `TargetKind` is `None`/`Boss` (there is nothing to further name: "the boss room" and "no member
/// downed" need no ref). `CountBand` is `null` on the six count-less templates named in §1
/// (`QuestCatalog.CountLessTemplates`) — this module's own rule, not derivable from `TargetKind`
/// alone (`extract-with-item-kind` has a real `TargetKind.ItemKind` yet is still count-less).
/// `Predicate` is the raw, already-parsed optional tree — `null` means "always eligible" (the seed
/// contract's own `none`), matching `EventRow.Eligibility`'s identical convention exactly.
///
/// <para>Deliberately narrow: `name`/`flavor`/`repeatScope`/`chainRef`/`prereqRefs` are real anchor
/// fields per the seed contract's own §1.5 table, but none of the four are named in this task's own
/// acceptance line, and guessing at `chainRef`/`prereqRefs`' exact same-kind-ref format here risks
/// encoding a wrong shape before the task that actually reads them (`QuestPreflight`, D4.13, per
/// §8's own "cyclic or cross-kind chainRef/prereqRefs") is built — left for that task to add
/// additively, matching this program's own "extend later, don't guess now" discipline.</para>
/// </summary>
public sealed record QuestRow(
    string QuestId,
    string TemplateId,
    string? TargetRef,
    string? CountBand,
    string RewardBand,
    string Scope,
    PredicateNode? Predicate);
