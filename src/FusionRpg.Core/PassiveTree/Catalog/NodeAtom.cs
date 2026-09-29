using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.PassiveTree.Catalog;

/// <summary>The ops a node atom may carry. The enum is the UNION of the two kind-specific op sets the
/// atom layer validates as raw strings (`AtomRowValidator.StatOps` / `DerivedOps`) — it is not itself
/// a per-kind allowance, and it must not be used as one:
///
/// <list type="bullet">
/// <item><c>stat.modify</c> (primary) — <c>Flat | Increased | More</c> (`AtomKindRegistry.cs:517`).</item>
/// <item><c>stat.derived</c> — <c>Flat | Increased | Replace | Flag</c>. There is **no `More`** on the
/// derived side (§6 M3): `AtomDerivedSubsystem.TryParseOp` has no `<c>more</c>` arm, so a `More`-op
/// derived atom would compose to nothing. M3 is enforced by a NAMED refusal at both the catalog loader
/// and `TreeBinderRun.ParseOp` — never by the absence of this member.</item>
/// </list>
///
/// <para><b>Why `More` exists here at all (P4.2, R2).</b> The two kinds share this one enum, so a
/// member absent for the derived side was also absent for the primary side — and the language stage
/// picked `more`-op primary affixes (e.g. `atom.savagery`, `atom.arm-hardening`) on real tree nodes,
/// every one of which was refused. The member was added and the derived-side refusal moved from a
/// structural property to an explicit kind-aware check, so it stays loud instead of decaying into the
/// silent drop `TreeAtomSource.BoundAtomsFor` performs when `TryParseOp` fails. The exact node and
/// family counts are a READING of the corpus at the revision the fix landed, not a constant to quote —
/// re-run `gk-forge/tools/TreeBinder --check` for the current numbers.</para>
/// </summary>
public enum NodeAtomOp
{
    Flat,
    Increased,

    /// <summary>`stat.modify` only (FA1's third op). Refused on `stat.derived` by name (§6 M3).</summary>
    More,

    Replace,
    Flag,
}

/// <summary>§2.4 — three-valued function of <see cref="UnitClass"/>, computed by the binder at bake
/// time and stored so the read path is a lookup, never a rederivation. Not a fourth channel
/// classification (DESIGN-GATE.md §34's warning) — a use of the third.</summary>
public enum ScaleAxis
{
    /// <summary>`GameUnits`, `GameUnitsPerSecond`, `ReciprocalPoints` — `kMicro · P(Θ_node) / 1e6`.</summary>
    PTheta,

    /// <summary>`SigmoidPoints`, `SigmoidMultiplierPoints`, `StatusPotencyPoints` — `kMicro · Θ_node / 1e6`
    /// (PS-3: contests read Θ, linear — never P(Θ), which would rise on the sheet with no
    /// multiplier effect).</summary>
    Theta,

    /// <summary>`PerMilleRatio` — flat per-mille points planned against the clamp. A bounded ratio,
    /// exempt from PS-8 by nature.</summary>
    FlatPermille,
}

/// <summary>
/// The magnitude fields of one node's granted atom — and there is no magnitude among them
/// (spec-tree-catalog.md §2.3). <c>kMicro</c> is the only number the catalog carries about
/// strength, and it is a coefficient (a per-million SHARE of <c>P(Θ)</c>), not a resolved value.
/// </summary>
public sealed record NodeAtom(
    string KindId,
    Effects.Atoms.AttachPoint AttachPoint,
    string ChannelId,
    NodeAtomOp Op,
    string? Trigger,
    string? WhenJson,
    long KMicro,
    ScaleAxis ScaleAxis,
    UnitClass UnitClass);
