using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Effects.Atoms.Generation;

/// <summary>
/// One affix-family entry (E43 input), reduced to the columns the primaryChannel/flatDerivedChannel
/// formula (bands.v1.json) actually reads, plus the family's own authored <c>tags</c> — the item
/// program's own authored surface (spec-family-expand.md §4: "reconcile and expand only"), carried
/// through so it can be stamped rather than dropped (`family-tags-closure`, D28, 2026-09-07). Roles,
/// frames, nameWords and notes remain out of scope here.
/// </summary>
/// <param name="Channel">Raw `params.channel` as authored. May be empty (a kind with no primary
/// channel), a concrete channel (`"maxHp"`), or an element-typed template (`"combat.power.{variant}"`)
/// — the <c>{variant}</c> marker is never expanded into concrete channels here (W7.9).</param>
/// <param name="Op">Raw `params.op` as authored (`"Flat"`/`"Increased"`/`"More"`/...), or null when the
/// kind carries no op.</param>
/// <param name="SourceFile">The affix-family file's own base name (e.g. <c>"g-life.json"</c>) — carried
/// into every emitted row's <c>tags.generatedFrom</c> (spec §3.2).</param>
/// <param name="Tags">The family's own authored <c>"tags"</c> array (e.g. <c>["offensive"]</c>) —
/// stamped as bare dictionary keys into every emitted row's `TagsJson`, alongside (never replacing)
/// the provenance keys. Required, not defaulted: a caller passes <see cref="Array.Empty{T}"/> for a
/// family with no tags, so an omission is a visible choice rather than a silent one (this codebase's
/// established convention for a field with only two real construction sites).</param>
public sealed record FamilyEntryInput(
    string Id, string Name, string KindId, string Channel, string? Op, string PowerBand, string SourceFile,
    IReadOnlyList<string> Tags);

/// <summary>
/// The generator-input balance surface (<c>data/seed/items/_tuning/tier-bands.v{n}.json</c> — the
/// LATEST published revision, chosen by the host through <c>TierBandsFile.FindLatestPath</c>) — the
/// ONLY place a per-channel <c>sharePermille</c> is authored (bands.v1.json's own
/// <c>sharePermilleOwnership</c> note). A channel stem absent from <see cref="ChannelWeightPermille"/>
/// has no authored share and E43 refuses that family rather than guess one.
///
/// <para>⚠ <b>Corrected 2026-09-23: this named <c>tier-bands.v1.json</c>, and so did the refusal
/// message beside it.</b> v1 authored <b>14</b> stems and was the reason every element-typed family
/// was refused; the file has since been published six times (v1 14 → v2 109 → v3 112 → v4 112 →
/// v5 125 → v6 144 stems, counted off the shipped files) and the generator reads the latest since the
/// 2026-09-08 <c>tier-bands-coverage</c> fix. A revision literal in a message that can fire against
/// any revision is the same defect in miniature.</para>
/// </summary>
public sealed record TierBandsInput(
    long BaseSharePermille,
    IReadOnlyDictionary<string, long> ChannelWeightPermille,
    IReadOnlyDictionary<string, long> OpWeightPermille);

/// <summary>One family E43 could not expand, with a reason an author can act on. Expected, reported
/// content — never a crash, and never silently dropped (spec §3.2 step 3).</summary>
public sealed record FamilyRefusal(string FamilyId, string Reason);

/// <summary>What one expansion pass produced: the rows it could emit, and every family it refused.</summary>
public sealed record FamilyExpansionResult(IReadOnlyList<AtomRow> Rows, IReadOnlyList<FamilyRefusal> Refusals);
