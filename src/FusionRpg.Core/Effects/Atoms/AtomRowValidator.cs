using System.Text.Json;
using System.Text.RegularExpressions;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// Judges one <see cref="AtomRow"/> before it reaches the table.
///
/// <para><b>Whole-row rejection.</b> There is no disabled-on-error state: a row is loadable or it is
/// refused with a typed reason an author can act on. A partially-applied atom is the silent no-op
/// this whole layer exists to remove.</para>
///
/// <para>Grammars are the ones pinned in definitions.md §1 — this is the only place they are
/// enforced, so drift shows up here rather than three modules later.</para>
/// </summary>
public static class AtomRowValidator
{
    // definitions.md §1. Compiled once: validation runs over whole seed files.
    static readonly Regex FamilyIdRe = new(@"^atom\.[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    static readonly Regex VariantRe = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    static readonly Regex AtomIdRe = new(@"^atom\.[a-z0-9-]+(\.[a-z0-9-]+)?\.t[1-9][0-9]*$", RegexOptions.Compiled);
    // Kebab, snake and dotted segments all pass. It was kebab-only, which was right while `icd_key`
    // was nothing but a grouping key — and wrong the moment E7 made it the compiled def's IDENTITY.
    // A migrated effect keeps the id its stored grants already name (`fx.butter_on_hit`), and that
    // id has both a dot and an underscore.
    static readonly Regex IcdKeyRe = new(@"^[a-z0-9]+([-_.][a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// FA1 spells the operation as the key: <c>flat</c>, <c>increased</c>, <c>more</c>. The atom
    /// schema spells it as a value of <c>op</c>, so the compiler translates — and can only translate
    /// what it recognises. An unvalidated op reached FA1 as neither key and applied a flat zero.
    /// </summary>
    public static readonly string[] StatOps = { "flat", "increased", "more" };

    /// <summary>Derived channels compose differently: no <c>more</c>, but <c>replace</c> and <c>flag</c>.</summary>
    public static readonly string[] DerivedOps = { "flat", "increased", "replace", "flag" };

    /// <summary>
    /// aura-skill T2 (audit D6): which <see cref="DerivedModifierOp"/> a
    /// <see cref="DerivedComposeKind"/> actually reads, mirroring each kind's op filter in
    /// <see cref="DerivedComposer.ComposeChannel"/> — the fold itself stays a pure switch, so this
    /// table exists to reject the mismatch where the authoring error originates (here, at bind/author
    /// time) rather than let <c>DerivedComposer</c> silently drop it.
    ///
    /// <para><b>Mirrors, does not replace.</b> `FlatSum` reads only <c>Flat</c>; `FlatReplace` reads
    /// <c>Flat</c> (the baseline sum) and <c>Replace</c> (which wins if present); `SumIncreased` reads
    /// only <c>Increased</c>; `MaxPriorityFlag` reads <c>Flag</c>/<c>Replace</c>/<c>Increased</c> —
    /// deliberately NOT <c>Flat</c>, which is why <c>AptitudeResolver</c>'s own comment calls emitting
    /// `Flat` there "the least-surprising fallback" for a case that cannot happen from authored content
    /// today (no shipped edge targets a `MaxPriorityFlag` channel) but is a programmatic emission, not
    /// an atom row — this table governs authored rows only and never touches that path.
    /// `DerivedComposeKindOpsTests` cross-checks every one of these 16 cells against the real composer,
    /// so the two cannot silently drift apart.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<DerivedComposeKind, string[]> DerivedComposeAcceptedOps =
        new Dictionary<DerivedComposeKind, string[]>
        {
            [DerivedComposeKind.FlatSum] = new[] { "flat" },
            [DerivedComposeKind.FlatReplace] = new[] { "flat", "replace" },
            [DerivedComposeKind.SumIncreased] = new[] { "increased" },
            [DerivedComposeKind.MaxPriorityFlag] = new[] { "flag", "replace", "increased" },
        };

    /// <summary>
    /// Validate one row. <paramref name="curveInput"/> resolves a curve id to the axis it reads;
    /// the store supplies one backed by <c>effect_curve</c>, which is how <b>D9</b> is enforced
    /// without Core ever touching SQL. Pass null to skip curve checks.
    ///
    /// <para><paramref name="composeKindOf"/> resolves a <c>stat.derived</c> row's <c>channel</c> param
    /// to its registered <see cref="DerivedComposeKind"/>, so a `flat`/`increased`/`replace`/`flag`
    /// mismatch against that channel's compose kind is rejected here rather than silently dropped at
    /// compose time (D6, aura-skill T2). Pass null to skip this check — the row still gets the plain
    /// "is `op` one of the four legal strings" check either way.</para>
    /// </summary>
    public static AtomRejection Validate(
        AtomRow row,
        Func<string, CurveInput?>? curveInput = null,
        Func<string, DerivedComposeKind?>? composeKindOf = null,
        Func<string, ChannelPoolRow?>? lookupPool = null)
    {
        if (row is null) return AtomRejection.Fail(AtomRejectionReason.BadParamValue, "null row");

        // ---- identity ------------------------------------------------------------------------
        if (!FamilyIdRe.IsMatch(row.FamilyId ?? ""))
            return Fail(AtomRejectionReason.BadParamValue, $"family_id '{row.FamilyId}' is not kebab-case with an atom. prefix");

        if (!string.IsNullOrEmpty(row.Variant) && !VariantRe.IsMatch(row.Variant))
            return Fail(AtomRejectionReason.BadParamValue, $"variant '{row.Variant}' is not kebab-case");

        if (row.Variant is null)
            return Fail(AtomRejectionReason.BadParamValue, "variant is NULL; use '' — NULL breaks the unique key");

        if (row.Tier < 1)
            return Fail(AtomRejectionReason.BadParamValue, $"tier {row.Tier} is below 1; there is no tier-0 parking spot");

        if (!AtomIdRe.IsMatch(row.AtomId ?? ""))
            return Fail(AtomRejectionReason.BadParamValue, $"atom_id '{row.AtomId}' does not match the grammar");

        // atom_id is DERIVED. Storing it is a denormalisation for lookup speed, so it must agree
        // with the columns it was derived from, or two sources of truth exist.
        var derived = row.DerivedId();
        if (!string.Equals(row.AtomId, derived, StringComparison.Ordinal))
            return Fail(AtomRejectionReason.IdMismatch, $"atom_id '{row.AtomId}' but columns imply '{derived}'");

        if (!string.IsNullOrWhiteSpace(row.IcdKey) && !IcdKeyRe.IsMatch(row.IcdKey!))
            return Fail(AtomRejectionReason.BadParamValue, $"icd_key '{row.IcdKey}' is not kebab-case");

        // ---- kind and params -------------------------------------------------------------------
        var kind = AtomKindRegistry.Get(row.KindId);
        if (kind is null)
            return Fail(AtomRejectionReason.UnknownKind, row.KindId ?? "(null)");

        if (!TryParseObject(row.ParamsJson, out var pars, out var parsErr))
            return Fail(AtomRejectionReason.BadParamValue, $"params_json: {parsErr}");

        // Through the REGISTRY, not straight to the schema. `ParamSchema.Validate` checks the shape —
        // which keys, of what kind — and the registry adds the rules that are about values, chief
        // among them G6: a `channel` outside the primary list. Calling the schema directly meant
        // `channel: "fireRate"` validated at load and then wrote nothing, which is the silent no-op
        // G6 was raised to close. The rule existed and had a test; the row path just never used it.
        var paramCheck = AtomKindRegistry.Validate(row.KindId, pars);
        if (!paramCheck.IsOk) return paramCheck;

        var opCheck = ValidateOp(row, pars, composeKindOf);
        if (!opCheck.IsOk) return opCheck;

        var poolCheck = ValidateChannelPoolRef(row, pars, lookupPool, composeKindOf);
        if (!poolCheck.IsOk) return poolCheck;

        // E2 wiring: a param the kind declares as a Value carries a value spec, and a spec whose
        // range runs backwards or whose roll policy does not exist must never reach the table.
        foreach (var def in kind.Params.Defs)
        {
            if (def.Kind != ParamKind.Value) continue;
            if (!pars.TryGetValue(def.Name, out var raw) || raw is not JsonElement el) continue;

            var specCheck = AtomJson.TryReadValueSpec(el, out var spec);
            if (!specCheck.IsOk) return Fail(specCheck.Reason, $"{def.Name}: {specCheck.Detail}");

            // `P0.2`: an event-linked magnitude is scoped to `resource.delta` — the kind lifesteal/
            // Corrosion content actually needs — so a marker never reaches a sink that has no idea
            // how to unwrap it (spec-value-spec-and-curve.md, "Event-linked magnitudes").
            if (spec.EventField is not null && !string.Equals(row.KindId, "resource.delta", StringComparison.Ordinal))
                return Fail(AtomRejectionReason.BadValueSpec,
                    $"{def.Name}: eventField is only authorable on resource.delta, not {row.KindId}");

            // T6.2 (`patron-absorption`): scoped to the two derived-magnitude kinds the migration
            // actually needs, same discipline as eventField's own resource.delta restriction — a
            // powerLadder marker never reaches a sink (e.g. a triggered runner atom) that has no
            // owner Θ in scope to resolve it against.
            if (spec.PowerLadder && row.KindId is not ("stat.modify" or "stat.derived"))
                return Fail(AtomRejectionReason.BadValueSpec,
                    $"{def.Name}: powerLadder is only authorable on stat.modify/stat.derived, not {row.KindId}");

            // T6.2's second gap: same scope as powerLadder, for the same reason — a compile-time
            // marker only a compiled stat write can consume.
            if (spec.ClampedLevelScale && row.KindId is not ("stat.modify" or "stat.derived"))
                return Fail(AtomRejectionReason.BadValueSpec,
                    $"{def.Name}: clampedLevelScale is only authorable on stat.modify/stat.derived, not {row.KindId}");

            // patron-absorption (spec-patron-absorption.md, 2026-09-06): same scope, same reason —
            // externalRef only resolves inside AtomCompiler.ResolvedParams' own compiled-atom path.
            if (spec.ExternalRef is not null && row.KindId is not ("stat.modify" or "stat.derived"))
                return Fail(AtomRejectionReason.BadValueSpec,
                    $"{def.Name}: externalRef is only authorable on stat.modify/stat.derived, not {row.KindId}");

            var curveCheck = ValidateCurve(def.Name, spec, curveInput);
            if (!curveCheck.IsOk) return curveCheck;
        }

        // ---- when ------------------------------------------------------------------------------
        if (!TryParseObject(row.WhenJson, out var when, out var whenErr))
            return Fail(AtomRejectionReason.BadParamValue, $"when_json: {whenErr}");

        var whenCheck = ValidateWhen(kind, when);
        if (!whenCheck.IsOk) return whenCheck;

        // E3 wiring: the predicate tree is validated HERE, at load. A tree nine levels deep or over
        // an unknown leaf is a row that must not land, not a runtime surprise to survive.
        if (when.TryGetValue("predicate", out var predRaw) && predRaw is JsonElement predEl)
        {
            var read = AtomJson.TryReadPredicate(predEl, out var tree);
            if (!read.IsOk) return Fail(read.Reason, read.Detail);

            var compiled = PredicateCompiler.TryCompile(tree, statusBit: null, out _);
            if (!compiled.IsOk) return Fail(compiled.Reason, compiled.Detail);
        }

        if (!TryParseObject(row.TagsJson, out _, out var tagsErr))
            return Fail(AtomRejectionReason.BadParamValue, $"tags_json: {tagsErr}");

        // ---- power ------------------------------------------------------------------------------
        // A stored override without a note is a magic number nobody can defend later.
        if (!string.IsNullOrWhiteSpace(row.PowerOverrideJson) && string.IsNullOrWhiteSpace(row.PowerNote))
            return Fail(AtomRejectionReason.MissingPowerNote, row.AtomId ?? "(no id)");

        // C3 (item-ideal.md, durable-ownership) — last, deliberately. row.Name is never validated
        // elsewhere: every other check in this file reads `def.Name`, a PARAMETER definition's name,
        // never the atom's own display name. An empty one loads clean today and only surfaces as a
        // blank line wherever the name is rendered. Placed after every structural/kind/param check so
        // a row with a real defect is refused for THAT reason, not shadowed by the missing name — a
        // more specific refusal is more useful to the author than a generic one. First real consumer
        // of ContentRuleViolated (§2b.1).
        if (string.IsNullOrWhiteSpace(row.Name))
            return AtomRejection.ContentRule("atom.empty-name", $"'{row.AtomId}' has no display name");

        return AtomRejection.Ok;

        AtomRejection Fail(AtomRejectionReason reason, string detail) =>
            AtomRejection.Fail(reason, $"{row.AtomId}: {detail}");
    }

    /// <summary>
    /// D9: an <c>OnApply</c> value scaled by a <b>level</b> curve cannot be rolled on the injector —
    /// it would need the curve's points and the actor's level, i.e. a content lookup at trigger time,
    /// which E19 forbids outright. E7 bakes pre-multiplied bounds instead, so this shape is refused
    /// at authoring rather than discovered when the compile/run split leaks.
    /// </summary>
    static AtomRejection ValidateCurve(
        string paramName, ValueSpec spec, Func<string, CurveInput?>? curveInput)
    {
        if (string.IsNullOrWhiteSpace(spec.CurveId) || curveInput is null) return AtomRejection.Ok;

        var input = curveInput(spec.CurveId!);
        if (input is null)
            return AtomRejection.Fail(AtomRejectionReason.BadCurve,
                $"{paramName}: unknown curve '{spec.CurveId}'");

        if (spec.Roll == RollPolicy.OnApply && input == CurveInput.Level)
            return AtomRejection.Fail(AtomRejectionReason.BadValueSpec,
                $"{paramName}: a level curve cannot scale an onApply value — the injector would need " +
                "the curve rows and the actor level per hit (D9). Use onInstantiate, or let E7 bake bounds.");

        return AtomRejection.Ok;
    }

    static AtomRejection ValidateWhen(AtomKind kind, IReadOnlyDictionary<string, object?> when)
    {
        // The trigger key is simply ABSENT for permanent modifiers — there is no "None" trigger name,
        // which is what keeps the closed count at 7 (definitions §14.2).
        var hasTrigger = when.TryGetValue("trigger", out var raw) && raw is not null;
        var trigger = hasTrigger ? raw!.ToString() : null;

        if (hasTrigger)
        {
            var t = AtomKindRegistry.ValidateTrigger(kind.KindId, trigger);
            if (!t.IsOk) return t;
        }
        else if (kind.Triggers.Count > 0 && !kind.TriggerOptional)
        {
            // The mirror case: a kind that fires on events must say which -- unless the kind itself
            // declares the trigger optional (A18e: stat.modify may be a permanent, no-trigger
            // modifier OR a triggered one, the one kind that needs both shapes at once).
            return AtomRejection.Fail(AtomRejectionReason.MissingParam,
                $"{kind.KindId} requires a trigger");
        }

        if (when.TryGetValue("chance", out var chance) && chance is not null)
        {
            if (!TryInt(chance, out var permille) || permille is < 0 or > 1000)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"chance must be per-mille in [0, 1000], got '{chance}'");
        }

        if (when.TryGetValue("icd_ms", out var icd) && icd is not null)
        {
            if (!TryInt(icd, out var ms) || ms < 0)
                return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                    $"icd_ms must be a non-negative integer, got '{icd}'");
        }

        return AtomRejection.Ok;
    }

    static bool TryInt(object? v, out int result)
    {
        switch (v)
        {
            case int i: result = i; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case JsonElement { ValueKind: JsonValueKind.Number } je: return je.TryGetInt32(out result);
            default: result = 0; return false;
        }
    }

    /// <summary>
    /// The operation vocabulary, checked at load because the compiler must translate it and cannot
    /// translate what it does not recognise.
    ///
    /// <para>An unrecognised <c>op</c> used to pass validation, compile to a <c>{channel, op,
    /// amount}</c> action, and reach FA1 — which reads <c>flat</c>/<c>increased</c>/<c>more</c> and
    /// nothing else. With none of them present the executor applies a <b>flat zero</b>
    /// (InjectorEffectActionSink.cs:104). Not a no-op: a real modifier of no size.</para>
    /// </summary>
    static AtomRejection ValidateOp(
        AtomRow row, IReadOnlyDictionary<string, object?> pars, Func<string, DerivedComposeKind?>? composeKindOf)
    {
        var allowed = row.KindId switch
        {
            "stat.modify" => StatOps,
            "stat.derived" => DerivedOps,
            _ => null,
        };
        if (allowed is null) return AtomRejection.Ok;

        if (!pars.TryGetValue("op", out var raw))
            return AtomRejection.Ok; // the schema already made it required; that check owns absence

        var op = (raw is JsonElement el && el.ValueKind == JsonValueKind.String ? el.GetString() : raw?.ToString())
                 ?.ToLowerInvariant();

        if (!Array.Exists(allowed, o => string.Equals(o, op, StringComparison.Ordinal)))
            return AtomRejection.Fail(AtomRejectionReason.BadParamValue,
                $"{row.AtomId}: op '{op}' is not one of {string.Join(" | ", allowed)}");

        // D6: `op` is one of the four legal strings, but for stat.derived that is necessary, not
        // sufficient — the TARGET CHANNEL's registered compose kind may not read this op at all
        // (e.g. `increased` on a FlatSum channel), in which case DerivedComposer would accept the row
        // and silently produce zero effect forever. Checked only when the caller supplies a resolver;
        // composeKindOf is null in every context that has no DerivedStatRegistry to ask (kept optional
        // like curveInput, not required, so this method has no hard dependency on Stats.Derived wiring).
        if (string.Equals(row.KindId, "stat.derived", StringComparison.Ordinal) && composeKindOf is not null)
        {
            if (!pars.TryGetValue("channel", out var channelRaw))
                return AtomRejection.Ok; // schema already requires it; absence is that check's job

            var channel = channelRaw is JsonElement channelEl && channelEl.ValueKind == JsonValueKind.String
                ? channelEl.GetString()
                : channelRaw?.ToString();
            if (string.IsNullOrEmpty(channel)) return AtomRejection.Ok;

            var kind = composeKindOf(channel);
            // E29 (spec-kind-value-guard.md §1.1, closed 2026-09-03): an unregistered channel is
            // AtomKindRegistry.Validate's job now — its generic Vocabulary check on stat.derived's
            // own `channel` ParamDef refuses it directly (BadParamValue), reading DerivedStatRegistry
            // fresh rather than a copy. This comment used to say "G6's job", but G6 was scoped to
            // stat.modify only and never actually ran for stat.derived — `crit.rat` for `crit.rate`
            // validated, bound, compiled, and wrote nothing forever until E29 closed that hand-off.
            if (kind is null) return AtomRejection.Ok;

            var acceptedOps = DerivedComposeAcceptedOps[kind.Value];
            if (!Array.Exists(acceptedOps, o => string.Equals(o, op, StringComparison.Ordinal)))
                return AtomRejection.Fail(AtomRejectionReason.ParamNotHonoured,
                    $"{row.AtomId}: op '{op}' on channel '{channel}' ({kind.Value}) is never read — " +
                    $"{kind.Value} composes only {string.Join(" | ", acceptedOps)}. This would bind and " +
                    "then apply nothing, which is the exact silent no-op this layer refuses (D6).");
        }

        return AtomRejection.Ok;
    }

    /// <summary>
    /// E30 (spec-channel-pool.md §3.3): the five load-time refusals for a pooled <c>channel</c>
    /// reference — <c>lookupPool</c> is caller-supplied (this stays free of I/O, matching
    /// <c>composeKindOf</c>'s own discipline), <c>null</c> in every context with no pool catalog to
    /// ask, in which case a pool-object channel value is not yet checkable and this is a no-op (the
    /// generic schema/vocabulary checks above have already accepted the row's SHAPE by this point;
    /// only a supplied <c>lookupPool</c> can judge whether the referenced pool actually exists).
    /// Skipped entirely for the concrete-channel form — that path is unchanged (§4: "the concrete
    /// form is not deprecated").
    /// </summary>
    static AtomRejection ValidateChannelPoolRef(
        AtomRow row, IReadOnlyDictionary<string, object?> pars,
        Func<string, ChannelPoolRow?>? lookupPool, Func<string, DerivedComposeKind?>? composeKindOf)
    {
        if (lookupPool is null) return AtomRejection.Ok;
        if (!pars.TryGetValue("channel", out var channelRaw) || channelRaw is null) return AtomRejection.Ok;

        var read = ChannelRefJson.TryRead(channelRaw, out var channelRef);
        if (!read.IsOk) return Fail(read.Reason, read.Detail);
        if (!channelRef.IsPool) return AtomRejection.Ok; // concrete form — nothing for this check to do

        // Rule 1: the pool id must exist.
        var pool = lookupPool(channelRef.PoolId!);
        if (pool is null)
            return Fail(AtomRejectionReason.BadParamValue, $"channel: unknown pool '{channelRef.PoolId}'");

        // Rule 2: every member must be a registered channel of the vocabulary THIS kind reads —
        // primary for stat.modify, derived for stat.derived, resource for resource.delta — read
        // fresh on every call, never cached (E29's own discipline, extended to pool members).
        IReadOnlyCollection<string>? vocabulary = row.KindId switch
        {
            "stat.modify" => AtomKindRegistry.PrimaryChannels,
            "stat.derived" => FusionRpg.Core.Stats.Derived.DerivedStatRegistry.CreateDefault()
                .AllRegistered.Select(d => d.ChannelId).ToArray(),
            "resource.delta" => FusionRpg.Core.Stats.Derived.DerivedStatChannels.ResourceIds,
            _ => null,
        };
        if (vocabulary is not null)
        {
            foreach (var member in pool.Members)
            {
                if (!vocabulary.Contains(member.Channel, StringComparer.Ordinal))
                    return Fail(AtomRejectionReason.BadParamValue,
                        $"channel: pool '{pool.PoolId}' member '{member.Channel}' is not a registered {row.KindId} channel");
            }
        }

        // Rule 3 (stat.derived only, mirroring ValidateOp's own scoping): every member's compose
        // kind must accept this atom's op — a pool whose members disagree is refused whole, never
        // partially honoured.
        if (string.Equals(row.KindId, "stat.derived", StringComparison.Ordinal) && composeKindOf is not null
            && pars.TryGetValue("op", out var opRaw))
        {
            var op = (opRaw is JsonElement opEl && opEl.ValueKind == JsonValueKind.String ? opEl.GetString() : opRaw?.ToString())
                     ?.ToLowerInvariant();
            foreach (var member in pool.Members)
            {
                var kind = composeKindOf(member.Channel);
                if (kind is null) continue; // an unregistered member already failed rule 2 above
                var acceptedOps = DerivedComposeAcceptedOps[kind.Value];
                if (!Array.Exists(acceptedOps, o => string.Equals(o, op, StringComparison.Ordinal)))
                    return Fail(AtomRejectionReason.ParamNotHonoured,
                        $"channel: pool '{pool.PoolId}' member '{member.Channel}' ({kind.Value}) does not accept op '{op}' — " +
                        $"a pool whose members disagree about which ops they accept is refused, not partially honoured");
            }
        }

        // Rule 4: count is floored at 1 (a structural bound — a draw of zero members is not an
        // effect, per AGENTS.md's no-hard-caps rule and its required comment), and above the member
        // count is a load-time refusal unless allowRepeat opts into it — never a silent clamp.
        if (channelRef.Count < 1)
            return Fail(AtomRejectionReason.BadParamValue, $"channel: pool '{pool.PoolId}' count {channelRef.Count} must be >= 1");
        if (channelRef.Count > pool.Members.Count && !channelRef.AllowRepeat)
            return Fail(AtomRejectionReason.BadParamValue,
                $"channel: pool '{pool.PoolId}' count {channelRef.Count} exceeds its {pool.Members.Count} members and allowRepeat is false");

        // Rule 5 (an empty members array) is structurally impossible here — ChannelPoolFile.TryParse
        // already refuses that at the POOL FILE's own load time, before any atom row could reference
        // a pool with zero members. Restated as a comment rather than a dead check.

        return AtomRejection.Ok;

        AtomRejection Fail(AtomRejectionReason reason, string detail) =>
            AtomRejection.Fail(reason, $"{row.AtomId}: {detail}");
    }

    static bool TryParseObject(
        string? json, out IReadOnlyDictionary<string, object?> map, out string error)
    {
        map = new Dictionary<string, object?>();
        error = "";

        if (string.IsNullOrWhiteSpace(json)) return true; // absent is legal; "{}" and null agree

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = $"expected an object, got {doc.RootElement.ValueKind}";
                return false;
            }

            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
                d[prop.Name] = prop.Value.Clone();
            map = d;
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
