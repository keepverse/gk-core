using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// One atom the Secondary runner owns. <b>E7 owns this contract; E15 runs it and E19 ships it.</b>
///
/// <para>Everything here is already resolved: the predicate is compiled to E13's chosen form, value
/// bounds are pre-multiplied by their curve, and status and element names are interned to ints. The
/// injector receives this and never needs a content row to decide anything — which is exactly why it
/// needs no content tables.</para>
/// </summary>
/// <param name="Predicate">
/// Compiled, never a tree. <see cref="PredicateCompiler.Always"/> when the atom has no condition.
/// </param>
/// <param name="ChanceMilli">Per-mille gate, 1000 when unconditional.</param>
/// <param name="IcdMs">Internal cooldown. Keyed on <paramref name="IcdKey"/>, not on this entry.</param>
/// <param name="IcdKey">
/// The compile-time grouping key. Atoms sharing one were merged into a single grant, so their clock
/// is shared by construction rather than by a runtime lookup (definitions §14.1).
/// </param>
/// <param name="Values">
/// Resolved value bounds per param. An <c>OnApply</c> range stays a range — that is the roll the
/// runner exists to make — but its curve has already been applied.
/// </param>
/// <param name="Limits">
/// The per-binding state keys that <b>caused</b> this atom to be runner work in the first place
/// (<see cref="Compilability"/>). Carrying the classification but not the keys would hand E15 an
/// entry it cannot execute — the classifier and the payload have to agree.
/// </param>
/// <param name="Params">
/// The non-numeric params a dispatch needs — <c>channel</c>, <c>element</c>, <c>currency</c>,
/// <c>status</c>. <see cref="Values"/> holds only what has a magnitude, so without these the runner
/// would know how much and not what.
/// </param>
public sealed record RunnerEntry(
    string AtomId,
    string KindId,
    string? Trigger,
    ICompiledPredicate Predicate,
    int ChanceMilli,
    int IcdMs,
    string IcdKey,
    IReadOnlyDictionary<string, ValueBounds> Values,
    RunnerLimits Limits,
    IReadOnlyDictionary<string, object?> Params)
{
    public bool IsUnconditional =>
        ChanceMilli >= 1000 && Limits == RunnerLimits.None
        && ReferenceEquals(Predicate, PredicateCompiler.Always);
}

/// <summary>
/// Per-binding limits. <see cref="None"/> is the absent set.
///
/// <para><b>Absent is not zero.</b> A cap of 0 means "never dispatch" and 0 charges means "already
/// spent" — both legal content. Absent is <c>-1</c> so an atom that declares nothing does not
/// silently become the most restrictive one.</para>
///
/// <para><c>MaxStacks</c> is carried rather than gated: it routes an atom here (it cannot be an
/// overlay key on a runner atom) but stacking is the grant's behaviour, not a proc gate. Carrying it
/// keeps it from being dropped on the way through.</para>
///
/// <para><b>No cooldown field.</b> spec-atom-runner.md lists a cooldown as distinct from an ICD, but
/// no kind schema declares a cooldown param and nothing routes on one — inventing a key here would
/// widen a closed vocabulary by convenience. Until a schema declares it, an ICD is the only clock.</para>
///
/// <para><b>No parameter defaults on purpose.</b> They read as if <c>new()</c> yields "absent", and it
/// does not — the implicit parameterless constructor of a record struct zeroes every field, which
/// under this encoding means <i>cap 0, charges 0</i>: the most restrictive limits there are. Every
/// call site passes all four, and <see cref="None"/> is spelled out.</para>
/// </summary>
public readonly record struct RunnerLimits(
    int CapPerMatch,
    int Charges,
    int EveryHits,
    int MaxStacks)
{
    public static readonly RunnerLimits None = new(-1, -1, -1, -1);

    public bool HasCap => CapPerMatch >= 0;
    public bool HasCharges => Charges >= 0;

    /// <summary>A meter of 1 is every hit, which is the same as no meter at all.</summary>
    public bool HasEveryHits => EveryHits > 1;
}

/// <summary>
/// A value's resolved bounds. <b>Curve-scaled already</b>: E19 forbids curve rows from travelling, so
/// the injector could not scale a value even if it wanted to (definitions §13 D9).
/// </summary>
public readonly record struct ValueBounds(int Min, int Max, RollPolicy Roll)
{
    public bool IsFixed => Min == Max;

    public static ValueBounds Of(ValueSpec spec, int multiplierMilli = 1000) =>
        new(CurveTable.ApplyMilli(spec.Min, multiplierMilli),
            CurveTable.ApplyMilli(spec.Max, multiplierMilli),
            spec.Roll);
}

/// <summary>
/// The baked output of one catalog revision: what Foundation runs, and what the runner runs.
///
/// <para>Baking happens <b>once per catalog revision</b>, not per bind — and the same revision must
/// produce identical bytes, or a push cannot be compared against what the injector already holds.</para>
/// </summary>
public sealed record CompiledCatalog(
    long CatalogRevision,
    IReadOnlyList<EffectDefDto> Defs,
    IReadOnlyList<EffectGrantDto> Compiled,
    IReadOnlyList<string> CompiledAtomIds,
    IReadOnlyList<RunnerEntry> Runtime,
    IReadOnlyList<CompileRejection> Rejected)
{
    /// <summary>
    /// Every atom id that reached a path — the completeness check that nothing was dropped.
    ///
    /// <para><see cref="CompiledAtomIds"/> is carried rather than derived: a def names its ICD group,
    /// and an action carries no id, so several atoms can hide behind one def.</para>
    /// </summary>
    public IEnumerable<string> AllAtomIds =>
        CompiledAtomIds.Concat(Runtime.Select(r => r.AtomId)).Concat(Rejected.Select(r => r.AtomId));
}

/// <summary>An atom neither path can execute here, with the reason an author needs.</summary>
public readonly record struct CompileRejection(string AtomId, AtomRejectionReason Reason, string Detail)
{
    public override string ToString() => $"{AtomId}: {Reason} — {Detail}";
}
