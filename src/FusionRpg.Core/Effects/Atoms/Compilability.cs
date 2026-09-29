using System.Text.Json;
using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>Which path an atom takes. Nothing is ever silently dropped — every atom lands in one.</summary>
public enum AtomPath
{
    /// <summary>An ordinary Foundation grant. Zero runtime cost — `EffectBag` already does this work.</summary>
    Compiled = 0,

    /// <summary>Needs the Secondary runner (E15): a predicate tree, a per-hit roll, or per-binding state.</summary>
    Runner,

    /// <summary>Neither path can execute it here. A rejection, never a silent no-op.</summary>
    Rejected,
}

/// <summary>Where an atom goes, and why — the reason is for an operator, not a debugger.</summary>
public readonly record struct Classification(AtomPath Path, string Reason, AtomRejectionReason Rejection)
{
    public static Classification Compiled(string reason) => new(AtomPath.Compiled, reason, AtomRejectionReason.None);
    public static Classification Runner(string reason) => new(AtomPath.Runner, reason, AtomRejectionReason.None);
    public static Classification Reject(AtomRejectionReason r, string reason) => new(AtomPath.Rejected, reason, r);

    public override string ToString() => $"{Path}: {Reason}";
}

/// <summary>
/// The pure classifier: given one atom, decide whether Foundation can already express it.
///
/// <para><b>The unit is the atom, not the binding.</b> Items have no behaviour; actors do
/// (definitions §0), so an item is just a source that puts atoms on an actor's list. There is no
/// binding-level coherence to preserve and each atom is judged alone.</para>
///
/// <para>This is a compiler, never an applier. Nothing here calls Unity, the Writer, or the bag.</para>
/// </summary>
public static class Compilability
{
    /// <summary>Kinds that map 1:1 to an FA opcode a sink implements. Internal (widened 2026-09-06,
    /// `ConstructionActionCompilabilityGuardTests`) so a guard test can assert this set never again
    /// drifts from <see cref="AtomCompiler.OpcodeOf"/>'s own switch — the fourth time the two lists
    /// disagreed, this session's own `structure.place` addition being the latest (see this field's
    /// own trailing comments for the first three).</summary>
    internal static readonly HashSet<string> OpcodeKinds = new(StringComparer.Ordinal)
    {
        "stat.modify", "resource.delta", "resource.economy", "status.apply", "status.clear",
        "shield.grant", "spawn.entity", "board.action", "grid.spawn", "grid.clear", "box.set",
        // aura-skill-todo.md Phase 5 / TC2 (2026-08-30). `stat.derived` now has an action --
        // EffectActions.ModifyDerivedStat -- so it belongs on the COMPILED path, not the runner path.
        // Without this entry Classify routes it to Runner ("has no FA opcode"), it never becomes an
        // EffectDef, and the lawn executor has nothing to read: the atom compiles to a runtime entry
        // that no derived consumer looks at. That is the fifth and last link in the chain.
        //
        // Unlike the other eleven, this opcode is DECLARATIVE: nothing executes it. A stat.derived
        // atom is a permanent modifier declaring no trigger, so the bag never fires it -- the grant's
        // presence is the effect, folded at resolve time. Hence no sink arm in either runtime.
        "stat.derived",

        // E35 (spec-match-modify.md §2.5): match.modify -> EffectActions.ModifyMatch. Params stay
        // {field, amount} on both the compiled and runner paths (ToOpcodeShape only rewrites
        // stat.modify/stat.derived), so this kind has no key-mismatch to guard against either.
        "match.modify",

        // E37 (spec-projectile-control.md §2b): bullet.modify -> EffectActions.BulletModify. Same
        // DECLARATIVE shape as stat.derived immediately below in the historical ordering of this set —
        // a permanent modifier (AtomTriggers.None) whose grant is read by a resolved-read reader
        // (GrantedBulletModifyAtomReader -> CheatPrefixes.BulletInitCheat), never fired by the bag.
        // Without this entry Classify would route every bullet.modify atom to the Runner path ("has no
        // FA opcode") even though OpcodeOf above resolves one — Classify's OpcodeKinds membership is a
        // SEPARATE gate from OpcodeOf, exactly the gap stat.derived's own comment documents. Params
        // stay {op, amount, bulletType, moveWay} on both paths — ToOpcodeShape only rewrites
        // stat.modify/stat.derived — so, like match.modify, there is no key-mismatch to guard here.
        "bullet.modify",

        // E36 (spec-wave-control.md §2.1) shipped wave.control -> EffectActions.WaveControl in
        // OpcodeOf but never added this entry — found while re-verifying E37's own bullet.modify fix
        // to this same set (this list and OpcodeOf are two SEPARATE gates, exactly the trap
        // stat.derived's comment above already documents). Without it every wave.control atom
        // silently routes to the Runner path ("has no FA opcode") and is never read there — the
        // ChainDepth-guarded, ExecWaveControl-shaped opcode E36 built never actually runs. Params
        // stay {op, wave, timerMs, enabled} on both paths — no key-mismatch to guard.
        "wave.control",

        // E41 (spec-ui-attach-point.md §2b): ui.present -> EffectActions.PresentUi. Unlike
        // stat.derived/bullet.modify immediately above, this one is NOT declarative — it carries
        // real triggers (AllTriggers) and a real per-fire executor (EffectBag.ExecPresentUi), the
        // same shape shield.grant already has. Params stay {op, amount, tag, bannerId, meterId,
        // ratio, durationMs} on both the compiled and runner paths (ToOpcodeShape only rewrites
        // stat.modify/stat.derived), so there is no key-mismatch to guard here either.
        "ui.present",

        // base-defense `siege-construction` 15.3b (2026-09-06): structure.place -> EffectActions.
        // PlaceStructure. THE FOURTH TIME this exact gap has bitten (stat.derived, bullet.modify,
        // wave.control above each found and fixed it independently) — this list and OpcodeOf are two
        // SEPARATE gates, and adding a kind to OpcodeOf alone routes every one of its atoms to the
        // Runner path silently ("has no FA opcode"), where AtomCompiler.EmitRunnerDefs would still
        // technically produce a working EffectDef, but never through the path this kind was actually
        // built and tested against. Found by a real, failing end-to-end test (ConstructionActionsTests),
        // not by inspection. NOT declarative, like ui.present/shield.grant: it carries a real trigger
        // (OnActivate) and a real per-fire executor (BattleEffectSink.ExecPlaceStructure). Params stay
        // {structureId, instant} on both paths (ToOpcodeShape only rewrites stat.modify/stat.derived),
        // so there is no key-mismatch to guard here either.
        "structure.place",
    };

    /// <summary>The only leaves a legacy grant overlay can express.</summary>
    static readonly HashSet<LeafId> LegacyLeaves = new() { LeafId.SideIs, LeafId.TypeIdIs, LeafId.ActorIsKiller };

    /// <summary>Per-binding state keys. `icd_ms` is deliberately absent — see <see cref="Classify"/>.</summary>
    static readonly string[] StatefulKeys = { "capPerMatch", "everyHits", "charges", "maxStacks", "max_stacks" };

    public static Classification Classify(AtomRow atom, RuntimeId runtime, bool hostIsPlanner = false)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null)
            return Classification.Reject(AtomRejectionReason.UnknownKind, atom.KindId ?? "(null)");

        // Rule 2 (first, because it is the only one that can REJECT): the target runtime must have a
        // consumer. A kind with none is refused here rather than compiled into something inert.
        var support = kind.SupportIn(runtime);
        if (support == RuntimeState.None)
            return Classification.Reject(AtomRejectionReason.RuntimeUnsupported,
                $"{atom.KindId} has no consumer in {runtime}");
        if (support == RuntimeState.PlanOnly && !hostIsPlanner)
            return Classification.Reject(AtomRejectionReason.RuntimeUnsupported,
                $"{atom.KindId} is plan-only in {runtime} and the host is not a planner");

        if (!OpcodeKinds.Contains(atom.KindId))
            return Classification.Runner($"{atom.KindId} has no FA opcode");

        var when = Read(atom.WhenJson);
        var pars = Read(atom.ParamsJson);

        // Rule 4: per-binding state. `icd_ms` alone does NOT count — EffectBag already enforces grant
        // ICD on the compiled path, so an ICD-only atom stays compilable. The runner owns ICD only
        // for atoms it already owns for some other reason.
        foreach (var key in StatefulKeys)
            if (when.ContainsKey(key) || pars.ContainsKey(key))
                return Classification.Runner($"needs per-binding state ({key})");

        // Rule 3: a per-hit roll needs a runner. OnApply where Min == Max is just Fixed.
        foreach (var def in kind.Params.Defs)
        {
            if (def.Kind != ParamKind.Value) continue;
            if (!pars.TryGetValue(def.Name, out var raw)) continue;
            if (!AtomJson.TryReadValueSpec(raw, out var spec).IsOk) continue;

            if (spec.Roll == RollPolicy.OnApply && spec.Min != spec.Max)
                return Classification.Runner($"{def.Name} rolls per hit ({spec.Min}-{spec.Max})");
        }

        // Rule 1: the predicate must reduce to filters a grant overlay already supports.
        if (when.TryGetValue("predicate", out var predEl) && predEl.ValueKind != JsonValueKind.Null)
        {
            var trigger = when.TryGetValue("trigger", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;

            var verdict = ReducesToLegacyFilters(predEl, trigger);
            if (verdict is not null) return Classification.Runner(verdict);
        }

        return Classification.Compiled("FT* trigger plus simple filters");
    }

    /// <summary>
    /// Null when the tree is expressible as legacy `filters`; otherwise the reason it is not.
    ///
    /// <para><b>The subject trap.</b> On <c>OnDamageDealt</c> the shipped overlay's `filters.side` and
    /// `filters.typeId` refer to the <b>damaged</b> entity — <c>ResolveFilterTarget</c> inverts side.
    /// So the legacy form means <c>subject: target</c> on that trigger, and an otherwise identical
    /// atom with <c>subject: self</c> is runner work. Compiling it would silently invert the filter,
    /// which is why this is a test rather than a comment.</para>
    /// </summary>
    static string? ReducesToLegacyFilters(JsonElement node, string? trigger)
    {
        if (node.ValueKind != JsonValueKind.Object) return "predicate is not an object";

        if (node.TryGetProperty("op", out var opEl))
        {
            var op = opEl.GetString()?.ToLowerInvariant();

            // Legacy filters are a conjunction and nothing else: no alternation, no negation.
            if (op != "and") return $"predicate uses '{op}' — a grant overlay has only AND";

            if (!node.TryGetProperty("children", out var kids) || kids.ValueKind != JsonValueKind.Array)
                return "malformed AND";

            foreach (var kid in kids.EnumerateArray())
            {
                var why = ReducesToLegacyFilters(kid, trigger);
                if (why is not null) return why;
            }
            return null;
        }

        if (!node.TryGetProperty("leaf", out var leafEl) || leafEl.ValueKind != JsonValueKind.String)
            return "malformed leaf";

        if (!Enum.TryParse<LeafId>(leafEl.GetString(), true, out var leafId) || !LegacyLeaves.Contains(leafId))
            return $"leaf '{leafEl.GetString()}' has no legacy filter";

        var subject = node.TryGetProperty("subject", out var sEl) && sEl.ValueKind == JsonValueKind.String
            ? sEl.GetString()?.ToLowerInvariant()
            : null;

        var expected = string.Equals(trigger, EffectTriggers.OnDamageDealt, StringComparison.Ordinal)
            ? "target"
            : "self";

        return string.Equals(subject, expected, StringComparison.Ordinal)
            ? null
            : $"leaf subject '{subject}' does not match what the legacy filter means on {trigger ?? "(no trigger)"} " +
              $"(expected '{expected}')";
    }

    static Dictionary<string, JsonElement> Read(string? json)
    {
        var d = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json)) return d;
        try
        {
            using var doc = JsonDocument.Parse(json!);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
            foreach (var p in doc.RootElement.EnumerateObject()) d[p.Name] = p.Value.Clone();
        }
        catch (JsonException) { /* E4 already refused this row */ }
        return d;
    }
}
