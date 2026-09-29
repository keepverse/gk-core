namespace FusionRpg.Core.BuildPresets;

/// <summary>
/// build-preset D2 (spec-preset-store.md): the closed vocabulary of things a build preset can point
/// at. A sixth kind is a reviewed change to this enum and to <c>BuildPresetShape</c>, never a new
/// string in a caller.
///
/// <para>Pure: no Unity, no Data, no server type. The ids are the persisted spellings and the one
/// parser lives beside them, so a saved row and a reader cannot drift on what "aptitudes" means.</para>
/// </summary>
public enum BuildPresetPieceKind
{
    /// <summary>Which creature is patron — 0 or 1 row, <c>target_ref</c> empty, <c>ref_id</c> a specimen
    /// <c>instance_id</c>.</summary>
    Patron = 1,
    /// <summary>The contract-bound set — 0 or ≥1 rows, ordinals <c>0..n-1</c>, unique ids, wardens
    /// outside the diff (D3).</summary>
    Field = 2,
    /// <summary>An aptitude preset per target scope — one row per <c>{scope}:{scopeKey}</c> target,
    /// <c>ref_id</c> an <c>rpg_aptitude_preset.preset_id</c>.</summary>
    Aptitudes = 3,
    /// <summary>An item loadout per equip target — <c>ref_id</c> an <c>rpg_item_loadout.loadout_id</c>.</summary>
    Gear = 4,
    /// <summary>The equipped action set for a loadout owner — ordinals <c>0..n-1</c>, <c>ref_id</c> an
    /// action id or a known aura id.</summary>
    Skills = 5
}

/// <summary>
/// The persisted ids of <see cref="BuildPresetPieceKind"/> and their one parser. Deliberately a
/// separate static class rather than <c>Enum.Parse</c>: an id stored by a future build must never make
/// a reader throw, and an unknown id must never be silently read as a real kind.
/// </summary>
public static class BuildPresetPieceKinds
{
    /// <summary>
    /// The one word an unparseable stored id resolves to (<see cref="Category"/>): a reader shows the
    /// row as a hole rather than throwing or dropping it (spec-preset-store.md "Validate on read").
    /// Deliberately <b>not</b> an enum member — the vocabulary is closed at five, and a sixth member
    /// would turn "I do not know this kind" into a value a caller could act on.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>The persisted id of a kind. Throws for a value outside the closed vocabulary, because
    /// that can only be a programming error, never stored data.</summary>
    public static string Id(BuildPresetPieceKind kind) => kind switch
    {
        BuildPresetPieceKind.Patron => "patron",
        BuildPresetPieceKind.Field => "field",
        BuildPresetPieceKind.Aptitudes => "aptitudes",
        BuildPresetPieceKind.Gear => "gear",
        BuildPresetPieceKind.Skills => "skills",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "unknown build-preset piece kind")
    };

    /// <summary>The one parser for a stored id. <c>false</c> for anything outside the closed
    /// vocabulary; <paramref name="kind"/> is then a value no round-trip can produce, so a caller that
    /// ignored the result still cannot mistake it for a real kind.</summary>
    public static bool TryParse(string? id, out BuildPresetPieceKind kind)
    {
        switch (id?.Trim())
        {
            case "patron": kind = BuildPresetPieceKind.Patron; return true;
            case "field": kind = BuildPresetPieceKind.Field; return true;
            case "aptitudes": kind = BuildPresetPieceKind.Aptitudes; return true;
            case "gear": kind = BuildPresetPieceKind.Gear; return true;
            case "skills": kind = BuildPresetPieceKind.Skills; return true;
            default:
                kind = default;
                return false;
        }
    }

    /// <summary>The stored id's own category for a reader that must not throw on a row written by a
    /// future build: a known id, or <see cref="Unknown"/>.</summary>
    public static string Category(string? storedId) =>
        TryParse(storedId, out var kind) ? Id(kind) : Unknown;
}
