using System.Text.Json;

namespace FusionRpg.Core.Creatures.Generation;

/// <summary>`redistribution-plan`'s own balance surface (`data/tuning/species-build.v{n}.json`,
/// tunables-ssot.md T1) — the parity band, the per-species lean range and its crowding sensitivity,
/// and the shape limits (`min`/`maxAptitudesPerSpecies`). Server-only host wiring, mirroring every
/// other generation-tool tuning file (`CreatureShapeTuning`) — the injector never plans a build.</summary>
/// <summary><see cref="RespecBasePrice"/>/<see cref="RespecEscalationPermille"/>/
/// <see cref="RespecDecayDays"/> — species-build-todo.md T4.1, spec-species-respec.md's own decision
/// 15: the price rises with the respec COUNT on that species and decays over time (churn, not
/// investment; never species level — that was decision 9, withdrawn by audit finding A2). Added
/// beside this file's existing redistribution-plan keys per the spec's own instruction ("shared with
/// m4 — add the three respec keys beside them; do not rewrite the file"), not a second tuning file, so
/// <see cref="RespecPolicy"/> reads the same hub every other species-build consumer already reads.</summary>
public sealed record SpeciesBuildTuning(
    int SchemaVersion, int Version,
    long ParityFloorPermille, long ParityCeilingPermille,
    long LeanMinPermille, long LeanMaxPermille,
    long CrowdingFactor, long SecondarySharePermille,
    int MaxAptitudesPerSpecies, int MinAptitudesPerSpecies,
    long RespecBasePrice, long RespecEscalationPermille, int RespecDecayDays,
    /// <summary>EP1.7 (spec-specimen-respec-price.md "Tunables", R18) — the unique-creature/commander
    /// respec price, published (v2) at the species keys' working values so a specimen re-spec starts
    /// at the price a player already knows. A distinct parameter set from <see cref="RespecBasePrice"/>
    /// et al: same formula (<see cref="Stats.Aptitudes.RespecPolicy.PriceOf"/>), a different base is a
    /// tunable, never a second curve.</summary>
    long UniqueRespecBasePrice, long UniqueRespecEscalationPermille, int UniqueRespecDecayDays,
    /// <summary>EP2.5 (`per-species-lean`, spec-per-species-lean.md "Tunables", R-Q8) — one weight per
    /// signal in <see cref="LeanSignals.Names"/>, in permille of lean. Ships at
    /// <see cref="LeanSignalWeights.Zero"/>, which makes the penalty exactly 0 and the planned vectors
    /// byte-identical to the pre-signal formula — the refactor's own proof. R6: `threatRung`'s weight
    /// ships at 0 and a later balance publish turns it on.</summary>
    LeanSignalWeights LeanSignalWeights,
    /// <summary>EP2.8 (`lead-relabel-pass`, spec-lead-relabel-pass.md "Phase 4 — the gate", R-Q6):
    /// the measured maximum lead share an aptitude may hold, per-mille of the measured population, plus
    /// the slack the gate allows on top of it. Working values, chosen against the measure artifact: the
    /// corpus fails the lead half today (413‰ against a 300‰ threshold), which is precisely what the
    /// relabel pass exists to fix. A CONCENTRATION threshold on a generated distribution, never a
    /// progression cap — no actor magnitude is bounded by these three numbers.</summary>
    /// <param name="LeadCapPermille">Null when the document predates the caps (v4 and earlier): the
    /// absence is STATED as absence, never silently defaulted to a number nobody published. The
    /// gate that reads them lands with the relabelled corpus (EP2.13/EP2.14), and it must refuse to
    /// gate on a document that has none.</param>
    /// <param name="ShapeCapPermille">The measured largest shape share a single build profile may
    /// hold, per-mille of the measured population (R-Q6's second half: a pass that trades one dominant
    /// aptitude for another while every species keeps its profile is what this catches). No tolerance
    /// term — Phase 4 compares it directly.</param>
    /// <param name="FreeRespecsPerEmpireLevel">`respec-free-counter` EP4.4 (spec-respec-free-counter.md,
    /// ruling R18) — how many earned free empire respecs ONE empire level pays.
    /// <see cref="Progression.EmpireLevelGrants.For"/> reads it through
    /// <see cref="Progression.EmpireLevelTuning"/>, which the host builds from this key at start.
    /// **0 is legal and means "levels pay nothing"** (the key is required from `v6` on, and every value
    /// is ≥ 0), which is why the grant function returns an EMPTY list rather than a zero-amount grant.
    /// Named for the empire level, never for a counter: R-Q3's fixed `respecFreeCount` 25 was never
    /// built and must not come back under this key.</param>
    long? LeadCapPermille = null, long? LeadCapTolerancePermille = null, long? ShapeCapPermille = null,
    long? FreeRespecsPerEmpireLevel = null)
{
    /// <summary>The gate's own threshold for the lead half — `leadCapPermille +
    /// leadCapTolerancePermille`, the one comparison Phase 4 makes (spec-lead-relabel-pass.md). Null
    /// when this document published no caps. Nothing refuses on it yet: Phase 4 lands with the
    /// relabelled corpus.</summary>
    public long? LeadRefusalPermille =>
        LeadCapPermille is { } cap && LeadCapTolerancePermille is { } tolerance ? cap + tolerance : null;

    /// <summary>The species view onto <see cref="Stats.Aptitudes.RespecPriceTuning"/> (EP1.6,
    /// spec-specimen-respec-price.md "One price function, re-typed to take its parameters") — same
    /// three numbers this record always carried, just handed to <see cref="Stats.Aptitudes.RespecPolicy.PriceOf"/>
    /// through its own shape instead of the whole tuning record. Byte-identical price.</summary>
    public FusionRpg.Core.Stats.Aptitudes.RespecPriceTuning SpeciesRespec =>
        new(RespecBasePrice, RespecEscalationPermille, RespecDecayDays);

    /// <summary>The unique-creature/commander view (EP1.7) — every quote a unique-creature or
    /// commander respec produces reads this, never <see cref="SpeciesRespec"/>. One formula, two
    /// parameter sets.</summary>
    public FusionRpg.Core.Stats.Aptitudes.RespecPriceTuning UniqueRespec =>
        new(UniqueRespecBasePrice, UniqueRespecEscalationPermille, UniqueRespecDecayDays);
}

public sealed class SpeciesBuildTuningRejection : Exception
{
    public SpeciesBuildTuningRejection(string message) : base(message) { }
}

/// <summary>
/// `per-species-lean`'s own balance surface: how much each of <see cref="LeanSignals.Names"/>' closed
/// signals may flatten a species' lean.
///
/// <para>A specialist (signal near 1000) pays nothing; a generalist loses up to `weight`, so sharp
/// builds belong to sharp creatures — the inversion R-Q8 exists to fix. Every weight is ≥ 0, so a
/// penalty only ever NARROWS a primary's share: nothing here can sharpen a build past the crowding
/// term it already has. `crowding` is deliberately NOT a member of this set — it is the existing
/// term and keeps its own published `crowdingFactor`; a `leanSignalWeights.crowding` key is refused
/// at load rather than silently ignored.</para>
/// </summary>
public sealed record LeanSignalWeights(long Specialisation, long Pure, long ThreatRung)
{
    /// <summary>The shipped value: every penalty 0, i.e. exactly the pre-signal formula.</summary>
    public static readonly LeanSignalWeights Zero = new(0, 0, 0);

    /// <summary>The weight of one signal, over the closed set <see cref="LeanSignals.Names"/> — a
    /// name outside it is a caller defect, never a silent zero.</summary>
    public long For(string signal) => signal switch
    {
        LeanSignals.Specialisation => Specialisation,
        LeanSignals.Pure => Pure,
        LeanSignals.ThreatRung => ThreatRung,
        _ => throw new ArgumentOutOfRangeException(
            nameof(signal), signal, $"not a lean signal; the closed set is {string.Join("/", LeanSignals.Names)}")
    };
}

/// <summary>Server-only host wiring (spec-redistribution-plan.md's own ⛔ callout): the generation
/// tool (`gk-forge/tools/CreatureBuildPlanGen`) reads `species-build.v{n}.json` directly by file path, exactly like
/// `CreatureSpeciesGen` reads its own tuning files — this hub exists for any FUTURE runtime consumer
/// (e.g. `creature-type-allocation`, module 5) that needs the band/lean values live rather than baked
/// into the committed plan, following this repo's own every-tuning-file-gets-a-hub convention. The
/// injector never configures this — m6's design is explicit that it receives points, never the plan,
/// the level, or the budget rule.</summary>
public static class SpeciesBuildTuningHub
{
    static SpeciesBuildTuning? _tuning;

    public static void Configure(SpeciesBuildTuning tuning) =>
        _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));

    public static SpeciesBuildTuning Tuning => _tuning ?? throw new InvalidOperationException(
        "SpeciesBuildTuningHub.Configure(...) has not run. The redistribution-plan band/lean values " +
        "read data/tuning/species-build.v{n}.json (tunables-ssot.md T5) — there is no built-in default to fall back to.");
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2).</summary>
public static class SpeciesBuildTuningLoader
{
    public static SpeciesBuildTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new SpeciesBuildTuningRejection("species build tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new SpeciesBuildTuningRejection($"species build tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            // Read once: the version decides which blocks are required, so every gated read below
            // shares this number rather than re-reading (and re-validating) it.
            var version = Int(root, "version");
            return new SpeciesBuildTuning(
                SchemaVersion: Int(root, "schemaVersion"),
                Version: version,
                ParityFloorPermille: Long(root, "parityFloorPermille"),
                ParityCeilingPermille: Long(root, "parityCeilingPermille"),
                LeanMinPermille: Long(root, "leanMinPermille"),
                LeanMaxPermille: Long(root, "leanMaxPermille"),
                CrowdingFactor: Long(root, "crowdingFactor"),
                SecondarySharePermille: Long(root, "secondarySharePermille"),
                MaxAptitudesPerSpecies: Int(root, "maxAptitudesPerSpecies"),
                MinAptitudesPerSpecies: Int(root, "minAptitudesPerSpecies"),
                RespecBasePrice: Long(root, "respecBasePrice"),
                RespecEscalationPermille: Long(root, "respecEscalationPermille"),
                RespecDecayDays: Int(root, "respecDecayDays"),
                UniqueRespecBasePrice: Long(root, "uniqueRespecBasePrice"),
                UniqueRespecEscalationPermille: Long(root, "uniqueRespecEscalationPermille"),
                UniqueRespecDecayDays: Int(root, "uniqueRespecDecayDays"),
                LeanSignalWeights: LeanWeights(root, version),
                LeadCapPermille: Cap(root, "leadCapPermille", version),
                LeadCapTolerancePermille: Cap(root, "leadCapTolerancePermille", version),
                ShapeCapPermille: Cap(root, "shapeCapPermille", version),
                FreeRespecsPerEmpireLevel: FreeRespecs(root, version));
        }
    }

    /// <summary>When the document's own `version` reached this, `freeRespecsPerEmpireLevel` must be
    /// present: v5 predates it (`respec-free-counter` EP4.4 published it at v6). The same version-gated
    /// shape as <see cref="CapsRequiredFromVersion"/> and for the same reason — the v1..v5 documents stay
    /// on disk so a revert is a file restore, and they must keep loading — while any document at or past
    /// the publish that introduced the key is refused when it lacks it (tunables-ssot.md T5).</summary>
    const int FreeRespecsRequiredFromVersion = 6;

    /// <summary>How many free empire respecs one empire level pays: a required whole number from
    /// <see cref="FreeRespecsRequiredFromVersion" />, at least 0 (0 = levels pay nothing), and `null` for
    /// a document that predates the key — an absence that is STATED, never silently defaulted to a
    /// number nobody published.</summary>
    static long? FreeRespecs(JsonElement root, int version)
    {
        if (!root.TryGetProperty("freeRespecsPerEmpireLevel", out var el)
            || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var value))
        {
            if (version >= FreeRespecsRequiredFromVersion)
                throw new SpeciesBuildTuningRejection(
                    $"species build tuning: v{version} is missing or non-integer 'freeRespecsPerEmpireLevel' " +
                    $"(required from v{FreeRespecsRequiredFromVersion})");
            return null;
        }
        if (value < 0)
            throw new SpeciesBuildTuningRejection(
                $"species build tuning: 'freeRespecsPerEmpireLevel' is {value} — a grant count is ≥ 0");
        return value;
    }

    /// <summary>When the document's own `version` reached this, the lead/shape caps must be present:
    /// v4 predates them. Same version-gated shape as <see cref="LeanWeightsRequiredFromVersion"/> and
    /// for the same reason — a legacy document on disk keeps loading, while any document at or past the
    /// publish that introduced the keys must carry them.</summary>
    const int CapsRequiredFromVersion = 5;

    /// <summary>One cap: required from <see cref="CapsRequiredFromVersion"/> and non-negative; null for
    /// a document that predates them (the absence is stated, never defaulted).</summary>
    static long? Cap(JsonElement root, string key, int version)
    {
        if (!root.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var value))
        {
            if (version >= CapsRequiredFromVersion)
                throw new SpeciesBuildTuningRejection(
                    $"species build tuning: v{version} is missing or non-integer '{key}' (required from v{CapsRequiredFromVersion})");
            return null;
        }
        if (value < 0)
            throw new SpeciesBuildTuningRejection($"species build tuning: '{key}' is {value} — a share threshold is ≥ 0");
        return value;
    }

    static int Int(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var v))
            throw new SpeciesBuildTuningRejection($"species build tuning: missing or non-integer '{key}'");
        return v;
    }

    /// <summary>When the document's own `version` reached this, `leanSignalWeights` must be present:
    /// v2 predates the signal mechanism, and reading it as all-zero is what the mechanism shipped as
    /// (its penalty is then 0, byte-identical). A v3+ document without the block is a defect, never a
    /// silent zero — but a legacy v2 file on disk must keep loading, so the requirement is version-
    /// gated rather than absolute.</summary>
    const int LeanWeightsRequiredFromVersion = 3;

    static LeanSignalWeights LeanWeights(JsonElement root, int version)
    {
        if (!root.TryGetProperty("leanSignalWeights", out var block) || block.ValueKind != JsonValueKind.Object)
        {
            if (version >= LeanWeightsRequiredFromVersion)
                throw new SpeciesBuildTuningRejection(
                    $"species build tuning: v{version} is missing 'leanSignalWeights' (required from v{LeanWeightsRequiredFromVersion})");
            return LeanSignalWeights.Zero;
        }

        var found = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var property in block.EnumerateObject())
        {
            if (!LeanSignals.Names.Contains(property.Name))
                throw new SpeciesBuildTuningRejection(
                    $"species build tuning: unknown signal 'leanSignalWeights.{property.Name}' — the closed set is {string.Join("/", LeanSignals.Names)}");
            if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt64(out var weight))
                throw new SpeciesBuildTuningRejection(
                    $"species build tuning: missing or non-integer 'leanSignalWeights.{property.Name}'");
            if (weight < 0)
                throw new SpeciesBuildTuningRejection(
                    $"species build tuning: 'leanSignalWeights.{property.Name}' is {weight} — a weight is ≥ 0");
            found[property.Name] = weight;
        }

        var missing = LeanSignals.Names.Where(name => !found.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
            throw new SpeciesBuildTuningRejection(
                $"species build tuning: missing key(s) 'leanSignalWeights.{string.Join("', 'leanSignalWeights.", missing)}'");

        return new LeanSignalWeights(found[LeanSignals.Specialisation], found[LeanSignals.Pure], found[LeanSignals.ThreatRung]);
    }

    static long Long(JsonElement parent, string key)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt64(out var v))
            throw new SpeciesBuildTuningRejection($"species build tuning: missing or non-integer '{key}'");
        return v;
    }
}
