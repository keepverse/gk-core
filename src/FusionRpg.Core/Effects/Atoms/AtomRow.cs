namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// One row of <c>effect_atom</c> — the SSOT base effect list. One row is one atom: the smallest
/// statement of what happens, with its numbers, its condition, and its power price.
///
/// <para>Code owns the logic; this row owns the values. Nothing here interprets anything: the store
/// round-trips it, <see cref="AtomRowValidator"/> judges it, and E7 compiles it.</para>
/// </summary>
public sealed record AtomRow
{
    /// <summary>Derived as <c>{family_id}[.{variant}].t{tier}</c> and validated against its columns.</summary>
    public string AtomId { get; init; } = "";

    /// <summary>Validated against the E1 registry; unknown is a load rejection, never a skip.</summary>
    public string KindId { get; init; } = "";

    /// <summary>Groups the tiers of one affix — <c>atom.fire-rider</c>.</summary>
    public string FamilyId { get; init; } = "";

    /// <summary>
    /// Discriminator within a family — element id, channel. <b>Empty string, never NULL</b>, when a
    /// family has one member: NULL in a unique key does not compare equal to itself in SQLite, so
    /// two "no variant" rows would both be accepted.
    /// </summary>
    public string Variant { get; init; } = "";

    /// <summary>Strength band within the family; 1 when a family has one tier.</summary>
    public int Tier { get; init; } = 1;

    public string Name { get; init; } = "";

    /// <summary>Trigger, `chance` ‰, `icd_ms`, and the E3 predicate tree.</summary>
    public string WhenJson { get; init; } = "{}";

    /// <summary>E1 schema-validated; numeric leaves are E2 value specs.</summary>
    public string ParamsJson { get; init; } = "{}";

    /// <summary>Element, family, category — for AI, UI, and cost lookup.</summary>
    public string TagsJson { get; init; } = "{}";

    /// <summary>Computed category vector. Nullable — <b>E9 lands eleven positions later</b> and backfills.</summary>
    public string? PowerJson { get; init; }

    /// <summary>Designer override.</summary>
    public string? PowerOverrideJson { get; init; }

    /// <summary>Required whenever <see cref="PowerOverrideJson"/> is set, so a magic number carries its reason.</summary>
    public string? PowerNote { get; init; }

    /// <summary>
    /// Compile-time grouping key, defaulting to <see cref="AtomId"/>. Atoms sharing one compile into
    /// a single grant whose triggers are the union of theirs (definitions §14.1). <b>Not</b> a runtime
    /// key — the ICD clock is keyed on the grant, and this is how several triggers reach one grant.
    /// </summary>
    public string? IcdKey { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Cache bust; joins the content hash (E8).</summary>
    public long Revision { get; init; }

    /// <summary>The id the columns imply. A stored <see cref="AtomId"/> that differs is `IdMismatch`.</summary>
    public string DerivedId() => DeriveId(FamilyId, Variant, Tier);

    public static string DeriveId(string familyId, string variant, int tier) =>
        string.IsNullOrEmpty(variant)
            ? $"{familyId}.t{tier}"
            : $"{familyId}.{variant}.t{tier}";

    /// <summary>The key E7 groups on.</summary>
    public string EffectiveIcdKey() => string.IsNullOrWhiteSpace(IcdKey) ? AtomId : IcdKey!;
}
