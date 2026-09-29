using System.Text.Json.Serialization;

namespace FusionRpg.Contracts;

/// <summary>
/// One instruction of a compiled predicate, on the wire (spec-compiled-push.md, E19).
///
/// <para>Every field is an int by the time it gets here: leaf ids, subjects, status bits and element
/// ordinals were all interned at compile time. That is the whole reason a predicate can travel at all
/// — the injector rebuilds the evaluator without ever seeing a content row, a status name, or an
/// element name.</para>
/// </summary>
public sealed class PredicateOpDto
{
    [JsonPropertyName("leaf")] public int Leaf { get; set; }
    [JsonPropertyName("subject")] public int Subject { get; set; }
    [JsonPropertyName("value")] public int Value { get; set; }

    /// <summary>Members of a set leaf (<c>typeIdIn</c>); null for the scalar leaves.</summary>
    [JsonPropertyName("set")] public List<int>? Set { get; set; }

    /// <summary>Where to jump. Negative values are answers, not indices.</summary>
    [JsonPropertyName("onTrue")] public int OnTrue { get; set; }
    [JsonPropertyName("onFalse")] public int OnFalse { get; set; }
}

/// <summary>A compiled predicate: the flat op array plus where to start. Empty means "always".</summary>
public sealed class CompiledPredicateDto
{
    [JsonPropertyName("ops")] public List<PredicateOpDto> Ops { get; set; } = new();
    [JsonPropertyName("entry")] public int Entry { get; set; }
}

/// <summary>A value's resolved bounds. Curve-scaled already — no curve row travels (D9).</summary>
public sealed class ValueBoundsDto
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }

    /// <summary>Roll policy ordinal. An <c>OnApply</c> range stays a range — that roll is the runner's.</summary>
    [JsonPropertyName("roll")] public int Roll { get; set; }
}

/// <summary>Per-binding limits. <c>-1</c> is absent; <c>0</c> is a real cap and a real charge count.</summary>
public sealed class RunnerLimitsDto
{
    [JsonPropertyName("capPerMatch")] public int CapPerMatch { get; set; } = -1;
    [JsonPropertyName("charges")] public int Charges { get; set; } = -1;
    [JsonPropertyName("everyHits")] public int EveryHits { get; set; } = -1;
    [JsonPropertyName("maxStacks")] public int MaxStacks { get; set; } = -1;
}

/// <summary>
/// One atom the Secondary runner owns, as delivered. This is E7's <c>RunnerEntry</c> flattened for
/// the wire — nothing here is a content row, and nothing here needs one to be executed.
/// </summary>
public sealed class RunnerEntryDto
{
    [JsonPropertyName("atomId")] public string AtomId { get; set; } = "";
    [JsonPropertyName("kindId")] public string KindId { get; set; } = "";
    [JsonPropertyName("trigger")] public string? Trigger { get; set; }
    [JsonPropertyName("chanceMilli")] public int ChanceMilli { get; set; } = 1000;
    [JsonPropertyName("icdMs")] public int IcdMs { get; set; }
    [JsonPropertyName("icdKey")] public string IcdKey { get; set; } = "";
    [JsonPropertyName("predicate")] public CompiledPredicateDto? Predicate { get; set; }
    [JsonPropertyName("values")] public List<ValueBoundsDto> Values { get; set; } = new();
    [JsonPropertyName("limits")] public RunnerLimitsDto Limits { get; set; } = new();

    /// <summary>The non-numeric params a dispatch needs — <c>channel</c>, <c>element</c>, <c>currency</c>.</summary>
    [JsonPropertyName("params")] public Dictionary<string, object?> Params { get; set; } = new();
}

/// <summary>
/// One runner binding: an entry plus how it arrived on an actor. Order is <c>(priority DESC,
/// bindingId ASC)</c> and the receiver re-sorts rather than trusting the wire, so two pushes of the
/// same set evaluate identically however the payload was serialised.
/// </summary>
public sealed class RunnerBindingDto
{
    [JsonPropertyName("bindingId")] public string BindingId { get; set; } = "";
    [JsonPropertyName("priority")] public int Priority { get; set; }
    [JsonPropertyName("ownerKey")] public string OwnerKey { get; set; } = "";
    [JsonPropertyName("entry")] public RunnerEntryDto Entry { get; set; } = new();
}

/// <summary>
/// The <c>effects.grants.apply</c> payload (spec-compiled-push.md, E19).
///
/// <para><b>Always the full set, never a delta.</b> Deltas need ordering guarantees a reconnect cannot
/// provide, and this is compiled output for one match rather than a catalog.</para>
///
/// <para><b>Cold.</b> This is the deploy/bind push, not a per-hit path. Nothing on the hot loop waits
/// for it, and a match with no pushed bindings runs with none — a normal state, not an error.</para>
/// </summary>
public sealed class AtomPushDto
{
    /// <summary>What the receiver ends up holding. It keeps what it has when this matches.</summary>
    [JsonPropertyName("catalogRevision")] public long CatalogRevision { get; set; }

    /// <summary>
    /// E8's stamp, carried so a mismatch is <b>visible in telemetry</b>. It never blocks delivery —
    /// an injector running against content the server has since edited is a diagnosable state, not a
    /// reason to leave a match unarmed.
    /// </summary>
    [JsonPropertyName("contentHash")] public string? ContentHash { get; set; }

    /// <summary>
    /// The per-match seed (definitions §13 <b>D5</b>). The dice are thrown locally so the hot loop
    /// never waits, but the server owns the seed — which is what makes those rolls replayable.
    /// </summary>
    [JsonPropertyName("matchSeed")] public ulong MatchSeed { get; set; }

    [JsonPropertyName("matchKey")] public string? MatchKey { get; set; }

    /// <summary>Compiled grants — the shape <c>EffectBag</c> already accepts.</summary>
    [JsonPropertyName("grants")] public List<EffectGrantDto> Grants { get; set; } = new();

    /// <summary>
    /// Defs for both paths. A runner dispatch names its atom id as an <c>effectId</c>, and the bag
    /// throws on an unknown one — so a runner entry without its def is a mid-flush failure far from
    /// the enqueue that caused it (found building E15).
    /// </summary>
    [JsonPropertyName("defs")] public List<EffectDefDto> Defs { get; set; } = new();

    [JsonPropertyName("runnerBindings")] public List<RunnerBindingDto> RunnerBindings { get; set; } = new();

    /// <summary>True when the receiver is already current and should keep what it holds.</summary>
    [JsonPropertyName("upToDate")] public bool UpToDate { get; set; }

    /// <summary>
    /// E26: the codec/emitter's own version, distinct from <see cref="ContentHash"/> and
    /// <see cref="CatalogRevision"/> on purpose. <c>ContentHash</c> covers seed DATA; a compiler-code
    /// change (like E26 emitting runner defs for the first time) moves nothing the content hash can
    /// see, which is exactly why `AtomImporter` reports "nothing changed" for a pure code edit. This
    /// field is what lets <c>AtomPushCodec.BuildPayload</c>'s short-circuit notice a code change even
    /// when the receiver's <c>catalogRevision</c> still matches. Always present, on every payload —
    /// including the <c>UpToDate</c> branch — so a receiver can learn and echo it back on its next
    /// Hello.
    /// </summary>
    [JsonPropertyName("emitterVersion")] public int EmitterVersion { get; set; }
}

/// <summary>What the injector says it already holds. Empty on cold start.</summary>
public sealed class AtomPushHelloDto
{
    [JsonPropertyName("catalogRevision")] public long CatalogRevision { get; set; }
    [JsonPropertyName("contentHash")] public string? ContentHash { get; set; }

    /// <summary>
    /// E26: what <see cref="AtomPushDto.EmitterVersion"/> the receiver last learned. Null (not 0) on a
    /// cold connect or a pre-E26 receiver that has never seen the field — <c>0</c> would collide with
    /// a real, if implausible, version 0 and silently skip the first re-push after an emitter change.
    /// </summary>
    [JsonPropertyName("emitterVersion")] public int? EmitterVersion { get; set; }
}
