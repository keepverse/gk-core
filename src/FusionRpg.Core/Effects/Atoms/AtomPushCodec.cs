using FusionRpg.Contracts;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// Turns E7's compiled output into the wire shapes and back (spec-compiled-push.md, E19).
///
/// <para><b>One codec, both ends.</b> The server encodes and the injector decodes with the same
/// code, so a field that survives one direction survives the other — a hand-written decoder on the
/// far side is how a dropped limit becomes an effect that silently never caps.</para>
///
/// <para><b>Nothing here is a content row.</b> A predicate travels as its flat int ops, values travel
/// as bounds with their curve already applied, and status and element names were interned at compile
/// time. If this codec ever needs an atom row to decode something, the compile/run split has leaked.</para>
/// </summary>
public static class AtomPushCodec
{
    /// <summary>
    /// E26: bumped by hand whenever THIS FILE's emit changes shape — the content hash covers seed
    /// DATA, so a compiler-code change (like E26 itself, which starts emitting runner defs for the
    /// first time) moves nothing the receiver's content hash or catalog revision can see. Without a
    /// separate version, a host already at the current <see cref="AtomPushDto.CatalogRevision"/> would
    /// never re-pull the new defs — the exact staleness trap `AtomImporter` already has for seed data,
    /// one layer up, reproduced here for compiler code (spec-runner-def-emit.md §3.3).
    /// </summary>
    public const int EmitterVersion = 2; // 1 = pre-E26 (compiled defs only, no runner defs on the wire)

    // ---- encode -----------------------------------------------------------------------------------

    public static RunnerEntryDto Encode(RunnerEntry entry)
    {
        if (entry is null) throw new ArgumentNullException(nameof(entry));

        var dto = new RunnerEntryDto
        {
            AtomId = entry.AtomId,
            KindId = entry.KindId,
            Trigger = entry.Trigger,
            ChanceMilli = entry.ChanceMilli,
            IcdMs = entry.IcdMs,
            IcdKey = entry.IcdKey,
            Predicate = EncodePredicate(entry.Predicate),
            Limits = new RunnerLimitsDto
            {
                CapPerMatch = entry.Limits.CapPerMatch,
                Charges = entry.Limits.Charges,
                EveryHits = entry.Limits.EveryHits,
                MaxStacks = entry.Limits.MaxStacks,
            },
        };

        // Ordinal-sorted so the same entry always serialises to the same bytes: a payload that
        // reorders between pushes cannot be compared against what the receiver already holds.
        foreach (var (name, bounds) in entry.Values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            dto.Values.Add(new ValueBoundsDto
            {
                Name = name,
                Min = bounds.Min,
                Max = bounds.Max,
                Roll = (int)bounds.Roll,
            });

        foreach (var (key, value) in entry.Params.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            dto.Params[key] = value;

        return dto;
    }

    public static RunnerBindingDto Encode(RunnerBinding binding) => new()
    {
        BindingId = binding.BindingId,
        Priority = binding.Priority,
        OwnerKey = binding.OwnerKey,
        Entry = Encode(binding.Entry),
    };

    /// <summary>Null when the atom has no condition — an empty op array would say the same thing twice.</summary>
    public static CompiledPredicateDto? EncodePredicate(ICompiledPredicate predicate)
    {
        if (predicate is not FlatPredicate flat) return null;

        var dto = new CompiledPredicateDto { Entry = flat.Entry };
        foreach (var op in flat.Ops)
            dto.Ops.Add(new PredicateOpDto
            {
                Leaf = (int)op.Id,
                Subject = (int)op.Subject,
                Value = op.Value,
                Set = op.Set?.ToList(),
                OnTrue = op.OnTrue,
                OnFalse = op.OnFalse,
            });

        return dto;
    }

    // ---- decode -----------------------------------------------------------------------------------

    public static RunnerEntry Decode(RunnerEntryDto dto)
    {
        if (dto is null) throw new ArgumentNullException(nameof(dto));

        var values = new Dictionary<string, ValueBounds>(StringComparer.Ordinal);
        foreach (var v in dto.Values)
            values[v.Name] = new ValueBounds(v.Min, v.Max, (RollPolicy)v.Roll);

        var pars = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in dto.Params) pars[key] = value;

        var limits = dto.Limits is null
            ? RunnerLimits.None
            : new RunnerLimits(dto.Limits.CapPerMatch, dto.Limits.Charges,
                dto.Limits.EveryHits, dto.Limits.MaxStacks);

        return new RunnerEntry(
            dto.AtomId,
            dto.KindId,
            dto.Trigger,
            DecodePredicate(dto.Predicate),
            dto.ChanceMilli,
            dto.IcdMs,
            string.IsNullOrEmpty(dto.IcdKey) ? dto.AtomId : dto.IcdKey,
            values,
            limits,
            pars);
    }

    public static RunnerBinding Decode(RunnerBindingDto dto) => new(
        dto.BindingId, dto.Priority, dto.OwnerKey, Decode(dto.Entry));

    public static ICompiledPredicate DecodePredicate(CompiledPredicateDto? dto)
    {
        if (dto is null || dto.Ops.Count == 0) return PredicateCompiler.Always;

        var ops = new FlatPredicate.Op[dto.Ops.Count];
        for (var i = 0; i < ops.Length; i++)
        {
            var o = dto.Ops[i];
            ops[i] = new FlatPredicate.Op(
                (LeafId)o.Leaf, (Subject)o.Subject, o.Value, o.Set?.ToArray(), o.OnTrue, o.OnFalse);
        }

        return FlatPredicate.FromOps(ops, dto.Entry);
    }

    // ---- the payload --------------------------------------------------------------------------------

    /// <summary>
    /// Build the full apply payload. <b>Always the full set</b> — a delta needs ordering guarantees a
    /// reconnect cannot provide, and this is one match's compiled output, not a catalog.
    ///
    /// <para>When the receiver's revision already matches, the payload carries nothing but
    /// <see cref="AtomPushDto.UpToDate"/>: it keeps what it holds rather than rebuilding an index for
    /// bindings it already has.</para>
    /// </summary>
    public static AtomPushDto BuildPayload(
        CompiledCatalog catalog,
        IEnumerable<RunnerBinding> bindings,
        ulong matchSeed,
        string? matchKey = null,
        string? contentHash = null,
        long? receiverRevision = null,
        int? receiverEmitterVersion = null)
    {
        if (catalog is null) throw new ArgumentNullException(nameof(catalog));

        var payload = new AtomPushDto
        {
            CatalogRevision = catalog.CatalogRevision,
            ContentHash = contentHash,
            MatchSeed = matchSeed,
            MatchKey = matchKey,
            // Always stamped, including the UpToDate branch below — a receiver that has never learned
            // this field (null, not 0) still needs to see it once so its NEXT Hello can echo it back.
            EmitterVersion = EmitterVersion,
        };

        // Two-term short-circuit (E26): the catalog revision alone cannot see a compiler-code change,
        // because ContentHash/CatalogRevision are both stamps over seed DATA. A receiver at the right
        // revision but the wrong (or unknown) emitter version still gets the full rebuild.
        if (receiverRevision == catalog.CatalogRevision && receiverEmitterVersion == EmitterVersion)
        {
            payload.UpToDate = true;
            return payload;
        }

        payload.Defs.AddRange(catalog.Defs);
        payload.Grants.AddRange(catalog.Compiled);

        // E26: one def per translatable runner entry, so AtomRunner's dispatch resolves against a
        // real def instead of throwing "unknown effect_id". Untranslatable entries are silently
        // dropped from the wire on purpose — AtomCompiler.EmitRunnerDefs already named each one's
        // reason in its own Rejected list, which is a server-side diagnostic, not a wire concern; the
        // corresponding RunnerBinding is skipped below (bindings, not entries, drive what is encoded).
        var (runnerDefs, _) = AtomCompiler.EmitRunnerDefs(bindings.Select(b => b.Entry));
        payload.Defs.AddRange(runnerDefs);

        // EmitRunnerDefs is exhaustive over its input (every distinct AtomId lands in exactly one of
        // Defs / Rejected), so "has a def" and "was not rejected" are the same test — filtering on the
        // def set alone is correct and needs no second lookup into the rejection list.
        var translatable = runnerDefs.Select(d => d.EffectId).ToHashSet(StringComparer.Ordinal);

        foreach (var binding in bindings
                     .Where(b => translatable.Contains(b.Entry.AtomId))
                     .OrderByDescending(b => b.Priority)
                     .ThenBy(b => b.BindingId, StringComparer.Ordinal))
            payload.RunnerBindings.Add(Encode(binding));

        return payload;
    }

    /// <summary>
    /// A delivered def, as the catalog holds it. Both compiled grants and runner dispatches name an
    /// <c>effectId</c>, and <c>EffectBag.Grant</c> throws on one the catalog has never seen — so the
    /// defs have to be merged before any grant is applied.
    ///
    /// <para><b>Params are unwrapped, not copied.</b> The real receive path
    /// (<c>CheatCommandRunner.InstallAtomPush</c>) deserializes with
    /// <c>JsonSerializer.Deserialize&lt;AtomPushDto&gt;</c> and no custom converter, so every
    /// <c>Dictionary&lt;string, object?&gt;</c> value in <c>EffectDefActionDto.Params</c> arrives as a
    /// boxed <see cref="System.Text.Json.JsonElement"/> — and <see cref="System.Text.Json.JsonElement"/>
    /// does not implement <see cref="IConvertible"/>, so every subsequent
    /// <c>JsonOverlay.GetInt</c>/<c>GetString</c>/<c>GetDouble</c>/<c>GetBool</c> call in
    /// <c>InjectorEffectActionSink</c> threw <see cref="InvalidCastException"/> for any def's static
    /// (non-overlay) param — caught by that class's own outer try/catch and logged as an execution
    /// failure, never surfaced as a crash. Found 2026-09-03 while working E28: reproduced with the
    /// real DTOs and this method, not a synthetic repro (<c>WireRoundTripProbe</c>, since deleted).
    /// <c>JsonOverlay.FromObject</c> already exists to unwrap exactly this shape (it is what
    /// <c>RunEffectGrant</c>'s hand-rolled overlay parse achieves a different way, by walking the raw
    /// <c>JsonElement</c> tree directly) — this is the codec's own half of the same fix.</para>
    /// </summary>
    public static EffectDef ToDef(EffectDefDto dto) => new()
    {
        EffectId = dto.EffectId,
        EffectType = dto.EffectType,
        Name = dto.Name,
        Enabled = dto.Enabled,
        SourceTag = dto.SourceTag,
        Triggers = new List<string>(dto.Triggers),
        Actions = dto.Actions
            .Select(a => new EffectActionRow
            {
                Seq = a.Seq,
                Action = a.Action,
                Params = FusionRpg.Core.Effects.JsonOverlay.FromObject(a.Params),
            })
            .ToList(),
    };

    /// <summary>Decode the runner half. The receiver re-sorts rather than trusting the wire order.</summary>
    public static List<RunnerBinding> DecodeBindings(AtomPushDto payload) =>
        payload.RunnerBindings.Select(Decode).ToList();
}
