using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Thresholds;

/// <summary>One <c>charm_resonance</c> row: <c>(axis, count_req) → container_id</c>. ssot-charms §4.2 —
/// this IS a breakpoint table, so it is the evaluator's input verbatim.</summary>
/// <param name="AuthoredContainerId">
/// The id exactly as the corpus ships it. Kept beside <see cref="ContainerId"/> so the padding
/// divergence is measurable rather than quietly normalised away.
/// </param>
public readonly record struct CharmResonanceRow(string Axis, int CountRequired, string ContainerId, string AuthoredContainerId)
{
    /// <summary>True when the shipped id is not the zero-padded canonical form.</summary>
    public bool IsAuthoredUnpadded => !string.Equals(ContainerId, AuthoredContainerId, StringComparison.Ordinal);
}

/// <summary>One charm the actor is currently holding, as far as the evaluator is concerned.</summary>
public readonly record struct HeldCharm(string ContainerId, string Axis);

/// <summary>
/// The charm-resonance consumer of <see cref="ThresholdEvaluator"/>. Same machine, different bucket key:
/// count the attuned charms sharing an <c>axis</c>, grant that axis's containers at its breakpoints.
///
/// <para><b>⭐ D33(a): resonance binds at <c>unique-actor:{specimenId}</c>.</b> ssot-charms §3.1 reverses
/// from option C (account-wide <c>player:</c>) to option B (per deployed actor). The evaluator is
/// scope-parametric by construction, so this is a configuration rather than a redesign — and the
/// consumer is no longer gated.</para>
///
/// <para><b>⛔ And it must never be <c>player:</c>-scoped while the stat layer reads it that way.</b>
/// <c>StatApplyScope.cs</c> returns <c>true</c> unconditionally for a <c>player:</c> owner
/// ("stub → match-wide apply") and <c>match</c> matches BOTH sides before it looks at <c>side</c>, so a
/// <c>player:</c>-scoped <c>+atk</c> charm on the lawn buffs the zombies. That is a correctness bug, not
/// a balance one. <see cref="RefuseUnsupportedScope"/> makes the refusal explicit rather than trusting
/// a caller to remember. The deeper defect — that an effect delivered through <c>StatApplyScope</c>
/// never consults the atom scope model at all, because the type has no field an atom could appear in —
/// is D33(b), filed against `buff-debuff-scope`, and it blocks nothing here.</para>
/// </summary>
public static class CharmResonance
{
    /// <summary>The five axes the shipped corpus uses. Read from the corpus, never transcribed.</summary>
    public static IReadOnlyList<string> AxesOf(IEnumerable<CharmResonanceRow> rows) =>
        rows.Select(r => r.Axis).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();

    static readonly Regex ResonanceIdRe = new("^charm\\.res-(?<axis>[a-z0-9]+(?:-[a-z0-9]+)*)-(?<count>[0-9]{1,2})$",
        RegexOptions.Compiled);

    /// <summary>
    /// Derive the breakpoint table from `gk-data/packs/fusion/data/seed/items/charms/resonance.json`.
    ///
    /// <para>The <c>count_req</c> is read from the id's own numeric tail — the corpus states the
    /// breakpoint nowhere else, and <c>ssot-charms</c> §4.2 defines the row as <c>(axis, count_req)</c>
    /// keyed exactly that way. The emitted <see cref="CharmResonanceRow.ContainerId"/> is the CANONICAL
    /// zero-padded form; the authored spelling is carried alongside so the divergence stays visible.</para>
    /// </summary>
    public static IReadOnlyList<CharmResonanceRow> DeriveTable(string resonanceJson)
    {
        if (string.IsNullOrWhiteSpace(resonanceJson))
            throw new CharmCorpusRejection("threshold.charm-corpus-malformed", "empty resonance document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(resonanceJson); }
        catch (JsonException ex)
        {
            throw new CharmCorpusRejection("threshold.charm-corpus-malformed", $"resonance: not valid JSON — {ex.Message}");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new CharmCorpusRejection("threshold.charm-corpus-malformed", "resonance: no 'entries' array");

            var rows = new List<CharmResonanceRow>();
            var seen = new HashSet<(string, int)>();

            foreach (var e in entries.EnumerateArray())
            {
                if (!e.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                    throw new CharmCorpusRejection("threshold.charm-corpus-malformed", "resonance: entry with no id");
                var authored = idEl.GetString()!;

                var m = ResonanceIdRe.Match(authored);
                if (!m.Success)
                    throw new CharmCorpusRejection("threshold.resonance-id-ungrammatical",
                        $"resonance id '{authored}' is not 'charm.res-{{axis}}-{{count}}'; the count_req is " +
                        "read from the id because the corpus states the breakpoint nowhere else");

                var axis = m.Groups["axis"].Value;
                var count = int.Parse(m.Groups["count"].Value);

                // Cross-check against the entry's own axis field: two spellings of one fact is how a
                // resonance ends up counting a different axis than the one it is named for.
                if (e.TryGetProperty("axis", out var axEl) && axEl.ValueKind == JsonValueKind.String
                    && !string.Equals(axEl.GetString(), axis, StringComparison.Ordinal))
                    throw new CharmCorpusRejection("threshold.resonance-axis-mismatch",
                        $"resonance '{authored}' names axis '{axis}' in its id and '{axEl.GetString()}' in its " +
                        "axis field");

                if (!seen.Add((axis, count)))
                    throw new CharmCorpusRejection("threshold.resonance-duplicate",
                        $"resonance table has two rows for (axis {axis}, count {count})");

                rows.Add(new CharmResonanceRow(axis, count,
                    ThresholdContainerIds.CharmResonance(axis, count), authored));
            }

            return rows;
        }
    }

    /// <summary>One axis's consumer. Per axis — never one merged count across axes.</summary>
    public static ThresholdConsumer<HeldCharm> Consumer(string axis, IEnumerable<CharmResonanceRow> table) =>
        new(
            SourceKey: ThresholdContainerIds.CharmResonanceSource(axis),
            BucketKey: c => string.Equals(c.Axis, axis, StringComparison.Ordinal) ? axis : null,
            Reducer: ThresholdReducer.Sum,
            Weight: _ => 1,
            Breakpoints: table
                .Where(r => string.Equals(r.Axis, axis, StringComparison.Ordinal))
                .OrderBy(r => r.CountRequired)
                .Select(r => new ThresholdBreakpoint(r.CountRequired, r.ContainerId))
                .ToList(),
            Buckets: Array.Empty<string>(),
            Priority: ThresholdContainerIds.CharmPriority);

    /// <summary>
    /// The scope gate, stated as code. A resonance binds at <see cref="OwnerKind.UniqueActor"/> and at
    /// nothing else — <c>player:</c> and <c>match</c> both reach the whole lawn today, and a set tier
    /// aimed anywhere but the wearer's own scope is <c>ScopeUnsupported</c> by ssot-sets §4.4 as well.
    /// </summary>
    public static AtomRejection RefuseUnsupportedScope(OwnerScope owner) => owner.Kind switch
    {
        OwnerKind.UniqueActor => AtomRejection.Ok,
        OwnerKind.Player => AtomRejection.Fail(AtomRejectionReason.ScopeUnsupported,
            "a charm resonance may not bind at player: scope — StatApplyScope returns match-wide for it " +
            "unconditionally, so a player-scoped +atk buffs the zombies (D33(a): bind at unique-actor:)"),
        OwnerKind.Match => AtomRejection.Fail(AtomRejectionReason.ScopeUnsupported,
            "a charm resonance may not bind at match scope — one actor's charms must not become a team buff"),
        _ => AtomRejection.Fail(AtomRejectionReason.ScopeUnsupported,
            $"a charm resonance binds at unique-actor: scope, not {OwnerScope.Name(owner.Kind)}:"),
    };
}
