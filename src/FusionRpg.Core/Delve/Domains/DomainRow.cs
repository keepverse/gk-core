namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// D4.15 (spec-domain-catalog.md §1) — one domain anchor's C# projection (`dungeon_domain`'s own
/// columns). `DangerBand` and `Entry` stay plain strings here, exactly like `QuestRow.Scope`/
/// `.CountBand`/`.RewardBand` already do in this same session's sibling module — `DomainCatalog.Load`
/// validates them, it does not transform this row into a second, enum-typed shape. Every other field
/// this module's own D4.15 acceptance line does not name (`layoutTemplateId`, `bossSpeciesRef`,
/// `retinueFamily`, `entranceHint`, `permadeathFromRung`, `firstClearRef`) is still carried verbatim —
/// `Load` refuses none of them beyond the two claims D4.15 itself makes (a known `dangerBand`, a legal
/// `entry`); their own cross-references are `DomainPreflight`'s later job (D4.17, §2 rows 2-3).
///
/// <para><see cref="FirstClearRef"/> added D3.15 (party-dungeon-todo.md, 2026-09-07) — the field
/// `DomainAnchor.FirstClearRef` (`Delve/Roll/DelveGraph.cs`, D4.28) already names as "a rung-80+
/// deterministic unique container id, or null for none", but `DomainRow`/`dungeon_domain` never carried:
/// the bank-at-clear wiring's own "which container" question resolves to THIS field. `null` is a
/// legitimate, expected value here (not a content gap to fill in later) — D4.28's own finding is that
/// only 4/144 unique anchors are concretely buildable today, so most real domains legitimately have no
/// relic to bind yet, matching `DomainAnchor`'s own identical "or null for none" convention exactly.
/// Defaults to `null` so every pre-existing 12-argument call site keeps compiling unchanged.</para>
/// </summary>
public sealed record DomainRow(
    string DomainId, string Name, string Flavor, string Theme, string Climate,
    string DangerBand, string Entry, string LayoutTemplateId, string BossSpeciesRef,
    string? RetinueFamily, string EntranceHint, string? PermadeathFromRung,
    string? FirstClearRef = null);
