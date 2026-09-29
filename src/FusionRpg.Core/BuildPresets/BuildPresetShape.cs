namespace FusionRpg.Core.BuildPresets;

/// <summary>
/// One piece row in its stored shape: which kind, who it applies to (<c>''</c> for a player-wide
/// piece), where it sits inside a multi-row piece, and what it points at. Pure — the store maps its
/// own columns onto this, and the validate-on-read layer adds the state that is not a shape question.
/// </summary>
public sealed record BuildPresetPieceRow(
    BuildPresetPieceKind Kind,
    string TargetRef,
    long Ordinal,
    string RefId);

/// <summary>
/// build-preset D2 (spec-preset-store.md "Save-time rules"): the SHAPE rules a build preset must
/// satisfy to be saved. Pure, structural, and deliberately shallow — whether a reference still exists,
/// whether capacity allows the field, whether a skill is held: those are the gates' questions, asked at
/// preview. A preset that was valid when saved and is not today is still readable.
///
/// <para>Nothing here reads the store. The one rule that needs a count — the soft max on create — is
/// enforced where the count lives (<c>RpgStore.BuildPresets</c>), by its own code
/// <c>build-preset.softMax</c>.</para>
/// </summary>
public static class BuildPresetShape
{
    /// <summary>The three scope words an <c>aptitudes</c> piece may name — the activate gate's own
    /// words (<c>AptitudePresetActivation.ScopeToAllocation</c>), never a second vocabulary.</summary>
    public static readonly IReadOnlyList<string> AptitudeScopes = new[] { "commander", "unique", "species" };

    /// <summary>
    /// The shape check, in a documented order so a caller that violates two rules gets a stable answer:
    /// the header's own name first (the request's first field), then emptiness, then each kind's shape
    /// in the vocabulary's order. Returns the empty string when the shape is legal.
    /// </summary>
    public static (bool Ok, string Reason) Validate(string? name, IReadOnlyList<BuildPresetPieceRow> pieces)
    {
        if (pieces is null) throw new ArgumentNullException(nameof(pieces));

        if (string.IsNullOrWhiteSpace(name))
            return (false, "build-preset.name.missing");
        if (pieces.Count == 0)
            return (false, "build-preset.empty");

        var patrons = pieces.Where(p => p.Kind == BuildPresetPieceKind.Patron).ToList();
        if (patrons.Count > 1)
            return (false, "build-preset.patron.multiple");

        var field = pieces.Where(p => p.Kind == BuildPresetPieceKind.Field).ToList();
        if (patrons.Count == 1 && field.Count > 0)
        {
            // The patron gate refuses an unbound creature, and the field piece releases whatever it
            // omits — so a patron outside the field would be released by the very same apply.
            var fieldIds = field.Select(f => f.RefId).ToHashSet(StringComparer.Ordinal);
            if (!fieldIds.Contains(patrons[0].RefId))
                return (false, "build-preset.patron.outside-field");
        }

        foreach (var group in Group(field))
            if (!Contiguous(group, requireUniqueIds: true))
                return (false, "build-preset.field.shape");

        foreach (var row in pieces.Where(p => p.Kind == BuildPresetPieceKind.Aptitudes))
        {
            var target = row.TargetRef ?? "";
            var split = target.IndexOf(':');
            if (split <= 0) return (false, "build-preset.aptitudes.scope");
            if (!AptitudeScopes.Contains(target[..split], StringComparer.Ordinal))
                return (false, "build-preset.aptitudes.scope");
        }

        foreach (var group in Group(pieces.Where(p => p.Kind == BuildPresetPieceKind.Skills).ToList()))
            if (!Contiguous(group, requireUniqueIds: false))
                return (false, "build-preset.skills.shape");

        return (true, "");
    }

    /// <summary>Rows of one multi-row kind, grouped by the target they apply to — contiguity is a
    /// per-target property, never a global one.</summary>
    static IEnumerable<List<BuildPresetPieceRow>> Group(IReadOnlyList<BuildPresetPieceRow> rows) =>
        rows.GroupBy(r => r.TargetRef ?? "", StringComparer.Ordinal)
            .Select(g => g.ToList());

    /// <summary>Ordinals exactly <c>0..n-1</c>, and — where the kind's rows are addressed by their own
    /// id rather than by position (field members) — no id repeated.</summary>
    static bool Contiguous(List<BuildPresetPieceRow> rows, bool requireUniqueIds)
    {
        if (rows.Count == 0) return true;
        var ordinals = rows.Select(r => r.Ordinal).ToHashSet();
        if (ordinals.Count != rows.Count) return false;
        for (var i = 0; i < rows.Count; i++)
            if (!ordinals.Contains(i)) return false;
        if (requireUniqueIds && rows.Select(r => r.RefId).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            return false;
        return true;
    }
}
