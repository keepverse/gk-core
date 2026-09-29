using System.Text.Json;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Stats.Aptitudes;

/// <summary>How an aptitude point reaches a channel — ssot-power-scale.md §4.6, Rule PS-3. Contest is
/// Θ-free (linear in the point share); Magnitude reads `P(Θ)` (grows with the ladder).</summary>
public enum AptitudeReadMode { Contest, Magnitude }

/// <summary>One edge: a channel fed by an aptitude, at a per-mille coefficient. Channels name their
/// source rather than aptitudes listing their channels (class-system-ideal.md §7a.1).</summary>
public sealed record AptitudeEdge(string Channel, string Source, long KMilli, AptitudeReadMode Mode);

public sealed record AptitudeGrant(long AptitudePointsPerThetaMilli, long SkillPointsPerThetaMilli);

/// <summary>class-system-todo.md P6.1, spec-point-economy.md §2.2: "the single number becomes a
/// table." A SEPARATE, new top-level block — not a restructuring of <see cref="AptitudeGrant"/> — on
/// purpose, so it never touches that record's own shape or the tests that already prove
/// `grant.aptitudePointsPerTheta` is required (AptitudeTuningTests.cs). One rate per
/// <see cref="AllocationScope"/> ("four grants, four sources" — §2.2); the caller supplies each
/// scope's own source value (`Θ_player` for Commander, almanac XP for CreatureType, `element_mastery` for
/// Aspect, specimen level for UniqueCreature — §2's table) since this module owns the RATE table, never
/// the sources themselves. Ordering (commander smallest, unique largest — §2.1) is asserted by
/// `PointBudgetTests` over the shipped file, not enforced here: a differently-ordered but otherwise
/// well-formed file is unbalanced, not malformed, and re-ordering it is `residual-fit`'s call.
///
/// <para><b>Not actually scaled by 1000 despite the "Milli" name</b> — matching the sibling
/// <see cref="AptitudeGrant.AptitudePointsPerThetaMilli"/>'s own real behavior: parsed via
/// <c>PositiveMilli</c>, which reads the raw JSON integer with no x1000 conversion (unlike
/// `spanPoints`, which genuinely goes through that conversion via `PositiveMilliFromDouble`). The
/// "Milli" suffix here is a naming-convention carry-over, not a unit; a shipped rate of `3` means
/// exactly 3 points per source unit. <see cref="PointBudget.PointsFor"/> multiplies directly — an
/// earlier draft divided by 1000 first and got an answer 1000x too small, caught by that type's own
/// tests.</para>
///
/// <para><see cref="RespecPrice"/> — class-system-todo.md P6.3, spec-point-economy.md §3: "the only
/// friction left, and it must not be a ban... available, unlimited, and costs a resource that
/// fighting also costs." A plain amount, not scoped by <see cref="AllocationScope"/> — the SAME price
/// regardless of which scope is being respecced. WHICH resource is spent is deliberately NOT here
/// (spec §8 "Ask first: which resource respec costs" — a mechanism choice, not a balance number);
/// <see cref="RespecPolicy"/> owns that as a documented placeholder. This field is only the "how
/// much," which IS the tunable (§6: "RespecPolicy... carries no bare literal — every number is a
/// named tunable").</para></summary>
/// <summary><see cref="SkillPointsPerThetaMilliByScope"/> — passive-tree C6, spec-tree-state.md §3
/// (D34): the SKILL-point sibling of <see cref="AptitudePointsPerThetaMilliByScope"/> above, added
/// 2026-09-06. <b>Deliberately OPTIONAL at the tuning-file level</b>, unlike its sibling: mirroring
/// the sibling's hard per-parse requirement would reject `aptitudes.v1-v5.json` and the dozen-plus
/// inline `AptitudeTuningLoader.Parse("""...""")` fixtures across the test suite that predate this
/// table, none of which grant scoped skill points. A tuning file that never carries this key parses
/// to an EMPTY dictionary here — not a guessed rate — and <see cref="PointBudget.SkillPointsFor"/> is
/// the one place that turns "no rate for this scope" into a loud rejection, at first use, naming the
/// scope (tunables-ssot.md T5's "never a default", applied at the point a caller actually needs the
/// number rather than at every historical file's parse). When the key IS present it is parsed exactly
/// like the sibling — all four scopes required, each a load rejection if absent.</summary>
public sealed record AptitudePointEconomy(
    IReadOnlyDictionary<AllocationScope, long> AptitudePointsPerThetaMilliByScope,
    long RespecPrice,
    IReadOnlyDictionary<AllocationScope, long> SkillPointsPerThetaMilliByScope);

/// <summary>class-system-todo.md P7.1-P7.3, spec-guard-economy.md §3/§5/§8 — the three coefficients
/// `PoiseLedger`/`Riposte` read (unified onto this one pair by battle-tempo `poise-unification`,
/// 2026-09-05 — the deleted `Combat/Guard/PoiseRuntime.cs` read the same three before then).
/// <see cref="FlatCommitCost"/>: Reading C's flat half, paid on every guard commit regardless of
/// outcome (§3: "committing is what costs, not landing"). <see cref="AbsorbDrainSharePermille"/>:
/// Reading C's proportional half, drained against what a guard actually stopped. <see cref="RiposteShareCapPermille"/>:
/// §5's conversion, a BOUNDED RATIO over an uncapped pool (PS-8 — the comment §8's code-style example
/// requires lives on <c>Riposte.DamageFromSpentPoise</c> itself, not here). All three are UNMEASURED
/// placeholders — §10 "Ask first: the riposte share, it is BASTION's whole offence" — shipped per the
/// same "shipping a guess is fine, calling it balance is not" posture this session already applied to
/// `AptitudePointEconomy`'s own tier weights and respec price.</summary>
public sealed record AptitudeGuardEconomy(long FlatCommitCost, long AbsorbDrainSharePermille, long RiposteShareCapPermille);

public sealed record AptitudeContestRead(long SpanPointsMilli, long ShareExponentMilli);

public sealed record AptitudeMagnitudeRead(long ShareExponentMilli);

/// <summary>
/// species-progression SP6.0 (species-layer-delivery step 6.1, R21) — the per-<see cref="AllocationScope"/>
/// weight the one resolver (`AptitudeResolver.Resolve`) applies to a layer's OUTPUT, never its share
/// (a bounded [0,1] ratio stays untouched; only the read value is scaled). A bounded ratio, not a cap:
/// any non-negative value loads, including above 1000.
///
/// <para><b>Absence is not a load rejection, and it is not a live-resolve rejection either</b> — the
/// same discipline this file's own sibling table, <see cref="AptitudePointEconomy.SkillPointsPerThetaMilliByScope"/>,
/// established at parse (`spec-tree-state.md` §3, D34), extended here all the way to <see cref="Of"/>
/// itself: an archived tuning file published before this table existed (`aptitudes.v1`-`v8`) stays
/// loadable AND resolvable. <b>Corrected 2026-09-19, species-progression step 6.1, against evidence,
/// not merely against style</b> — an earlier draft of this type had <see cref="Of"/> throw at first use
/// for a scope the table does not carry, on the theory that "never falls back to 1000 silently" should
/// hold all the way to the real resolve path, matching <see cref="PointBudget.SkillPointsFor"/>'s own
/// sibling refusal. That theory did not survive contact with the actual call graph: dozens of real
/// regression fixtures — <c>TerminationGuardTests</c>, <c>DominanceGuardTests</c>, <c>BossBuildTests</c>,
/// <c>ZombossPatternTests</c>, <c>ChannelModsHubParityTests</c>, <c>DominanceBaselineTests</c>,
/// <c>ProveAptitudeJsonEmitTests</c> — deliberately pin an exact OLD file (`aptitudes.v1.json`,
/// `v2.json`) and then genuinely RESOLVE against it, on purpose, to prove a numbered historical
/// behaviour never drifts; that is a stronger and more common real usage than the "nothing resolves
/// against a frozen snapshot" assumption the throwing design was built on. <b>The fix that actually
/// fits the evidence:</b> <see cref="Of"/> reads a missing scope as 1000 — the unweighted identity, i.e.
/// exactly the pre-R21 read those pinned files were always meant to reproduce — never a rejection.
/// This carries NO silent-typo risk for a CURRENT, intentionally-weighted file: <see cref="Of"/> only
/// ever sees an empty table when the WHOLE BLOCK was absent at parse (see below), because a PRESENT
/// block already fails loudly at parse for a missing scope key. There is no partial, "some scopes
/// weighted, one silently defaulted" table this default could ever mask.</para>
///
/// <para>If the block IS present, it is validated strictly and fails BY NAME at parse: a missing
/// scope key, an unknown scope key, or a negative weight are each a rejection — never a silent
/// coercion or a partial table.</para>
/// </summary>
public sealed record AptitudeLayerWeights(IReadOnlyDictionary<AllocationScope, long> WeightMilliByScope)
{
    public long Of(AllocationScope scope) => WeightMilliByScope.TryGetValue(scope, out var w) ? w : 1000;
}

public sealed record AptitudeRead(AptitudeContestRead Contest, AptitudeMagnitudeRead Magnitude, AptitudeLayerWeights LayerWeights);

/// <summary>The termination-invariant dial (class-system-ideal.md §5d). One multiplier over every
/// recovery family, because `r = recovery/peerDamage` is a GLOBAL ratio.</summary>
public sealed record AptitudeRecovery(long ScaleMilli, long TargetRecoveryShareMilli, IReadOnlyList<string> Families);

/// <summary>class-system-todo.md P8.3: the termination invariant's OWN sibling dial for
/// non-recovery survivability — <c>combat.defense</c>/<c>dodge</c>/<c>parry</c>/<c>block</c>/
/// <c>absorption</c>/<c>heal</c>. Found 2026-08-27: a 12-corner sweep of every pair (not just the one
/// hand-picked Vigor-vs-Bulwark case P5.2 caught) showed 30 of 66 unordered pairs unending, not one —
/// eight aptitudes share an identical, near-zero <c>combat.power.omni</c> floor (none of them source a
/// direct offense edge) and form a perfect mutual-stalemate clique against each other. `Recovery`
/// alone cannot close it without creating an ABSOLUTE dominant corner (measured: cutting
/// `recovery.scaleMilli` far enough to zero every violation makes `Might` beat all eleven others
/// outright) — because four of the eight lean on defense/dodge/parry-block/heal-power, not hp-regen,
/// as their PRIMARY survival stat (<c>Bulwark</c>'s own parry/block kMilli dwarfs its hp-regen
/// contribution), and `Recovery.Families` never touched those channels. A SEPARATE, independently
/// sized dial for exactly those channels — same shape as <see cref="AptitudeRecovery"/>, sharing
/// nothing but the pattern — is what let both invariants close together (§5d.4b: "coupled... must be
/// solved jointly") without touching `Recovery.ScaleMilli` at all. Deliberately no
/// `TargetMitigationShareMilli` field: unlike recovery's own closed-form `r = recovery/peerDamage`,
/// this dial's target was found by direct 12x12 measurement, not a single ratio a future reader could
/// re-derive from one number — the search itself, not a target constant, is the record of how it was
/// solved (docs/research/class-residual-2026-08-27.md).</summary>
public sealed record AptitudeMitigation(long ScaleMilli, IReadOnlyList<string> Families);

/// <summary>
/// The class system's whole balance surface, loaded from `data/tuning/aptitudes.v{n}.json` — ported
/// 2026-08-26 (class-system P2.1) from this program's own POC, `gk-core/tools/CombatSim/tuning/aptitudes.v1.json`
/// / `AptitudeTuning.cs`. Four blocks: <see cref="Grant"/> is the point ECONOMY, <see cref="Read"/> is
/// the two SCALE functions (PS-3), <see cref="FamilyRead"/> is the `unitClass`-adjacent DECISION per
/// family (which read mode it takes), <see cref="Edges"/> is the DISTRIBUTION.
///
/// <para><b>Pure parser, no file I/O</b> (tunables-ssot.md §7.2) — <see cref="AptitudeTuningLoader.Parse"/>
/// takes a string; a host reads the file and calls it, exactly like <c>ProgressionTuningLoader</c>.
/// <b>Every key is required.</b> A missing one is a load rejection naming it, never a built-in default.</para>
/// </summary>
public sealed record AptitudeTuning(
    int SchemaVersion, int Version,
    AptitudeGrant Grant, AptitudeRead Read, AptitudeRecovery Recovery,
    IReadOnlyDictionary<string, AptitudeReadMode> FamilyRead,
    IReadOnlyList<AptitudeEdge> Edges,
    AptitudePointEconomy PointEconomy,
    AptitudeGuardEconomy GuardEconomy,
    AptitudeMitigation Mitigation,
    int DroppedRetiredEdges = 0)
{
    /// <summary>
    /// How many edges this parse dropped because their channel is in
    /// <see cref="DerivedStatChannels.Retired"/> (`retire-atk` R2). Every caller — a host on the live
    /// version, the species bake on `aptitudes.v2`, a test on any version — gets clean edges from this
    /// one place, and the count is what lets a test prove the drop happened instead of trusting it.
    /// Zero for a version that never named a retired channel.
    /// </summary>
    /// <summary>
    /// Which <see cref="FamilyRead"/> row governs a channel, or null if none does.
    ///
    /// <para><b>Exact match first, then strip one axis suffix.</b> `combat.parry.strength.omni` →
    /// `combat.parry.strength` (the element is the arena, never part of the scale class), and
    /// `resource.max.hp` → `resource.max`. But `move.range` and `progression.xpRate` carry NO axis, so
    /// blind stripping would look up `move`/`progression` and reject a perfectly classified channel —
    /// try the whole id before assuming it has a suffix.</para>
    /// </summary>
    public string? FamilyOf(string channel)
    {
        if (FamilyRead.ContainsKey(channel)) return channel;
        var dot = channel.LastIndexOf('.');
        if (dot <= 0) return null;
        var stripped = channel[..dot];
        return FamilyRead.ContainsKey(stripped) ? stripped : null;
    }
}

public sealed class AptitudeTuningRejection : Exception
{
    public AptitudeTuningRejection(string message) : base(message) { }
}

/// <summary>Pure parser, no file I/O (tunables-ssot.md §7.2) — a host reads
/// `data/tuning/aptitudes.v{n}.json` and calls <see cref="Parse"/>.</summary>
public static class AptitudeTuningLoader
{
    static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static AptitudeTuning Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new AptitudeTuningRejection("aptitude tuning: empty document");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { throw new AptitudeTuningRejection($"aptitude tuning: not valid JSON — {ex.Message}"); }

        using (doc)
        {
            var root = doc.RootElement;
            var schemaVersion = Int(root, "schemaVersion", "$");
            var version = Int(root, "version", "$");

            var grantEl = Obj(root, "grant", "$");
            var grant = new AptitudeGrant(
                AptitudePointsPerThetaMilli: PositiveMilli(grantEl, "aptitudePointsPerTheta", "grant"),
                SkillPointsPerThetaMilli: NonNegativeMilli(grantEl, "skillPointsPerTheta", "grant"));

            var readEl = Obj(root, "read", "$");
            var contestEl = Obj(readEl, "contest", "read");
            var magnitudeEl = Obj(readEl, "magnitude", "read");
            var read = new AptitudeRead(
                Contest: new AptitudeContestRead(
                    SpanPointsMilli: PositiveMilliFromDouble(contestEl, "spanPoints", "read.contest"),
                    ShareExponentMilli: PositiveMilli(contestEl, "shareExponentMilli", "read.contest")),
                Magnitude: new AptitudeMagnitudeRead(
                    ShareExponentMilli: PositiveMilli(magnitudeEl, "shareExponentMilli", "read.magnitude")),
                LayerWeights: ParseLayerWeights(readEl));

            var recoveryEl = Obj(root, "recovery", "$");
            var families = StringArray(recoveryEl, "families", "recovery");
            if (families.Count == 0)
                throw new AptitudeTuningRejection("aptitude tuning: missing required key 'recovery.families'");
            var recovery = new AptitudeRecovery(
                ScaleMilli: NonNegativeMilli(recoveryEl, "scaleMilli", "recovery"),
                TargetRecoveryShareMilli: PositiveMilli(recoveryEl, "targetRecoveryShareMilli", "recovery"),
                Families: families);

            var familyReadEl = Obj(root, "familyRead", "$");
            var familyRead = new Dictionary<string, AptitudeReadMode>(StringComparer.Ordinal);
            foreach (var prop in familyReadEl.EnumerateObject())
            {
                if (prop.Name.StartsWith('_')) continue; // notes, never data
                // `retire-atk` R2: a `familyRead` key naming a retired channel is ignored, not rejected.
                // Every archived version keeps its full historical text and stays loadable; the retired
                // id is simply not part of the parsed surface any more (DerivedStatChannels.Retired).
                if (DerivedStatChannels.Retired.Contains(prop.Name)) continue;
                var raw = prop.Value.GetString() ?? "";
                familyRead[prop.Name] = raw.ToLowerInvariant() switch
                {
                    "contest" => AptitudeReadMode.Contest,
                    "magnitude" => AptitudeReadMode.Magnitude,
                    _ => throw new AptitudeTuningRejection($"aptitude tuning: familyRead['{prop.Name}'] has unknown read mode '{raw}' — expected 'contest' or 'magnitude'")
                };
            }
            if (familyRead.Count == 0)
                throw new AptitudeTuningRejection("aptitude tuning: missing required key 'familyRead'");

            var pointEconomyEl = Obj(root, "pointEconomy", "$");
            var byScopeEl = Obj(pointEconomyEl, "aptitudePointsPerThetaMilliByScope", "pointEconomy");
            const string byScopePath = "pointEconomy.aptitudePointsPerThetaMilliByScope";
            var pointEconomy = new AptitudePointEconomy(
                AptitudePointsPerThetaMilliByScope: new Dictionary<AllocationScope, long>
                {
                    [AllocationScope.Commander] = PositiveMilli(byScopeEl, "commander", byScopePath),
                    [AllocationScope.CreatureType] = PositiveMilli(byScopeEl, "creatureType", byScopePath),
                    [AllocationScope.Aspect] = PositiveMilli(byScopeEl, "aspect", byScopePath),
                    [AllocationScope.UniqueCreature] = PositiveMilli(byScopeEl, "uniqueCreature", byScopePath),
                },
                RespecPrice: PositiveMilli(pointEconomyEl, "respecPrice", "pointEconomy"),
                SkillPointsPerThetaMilliByScope: OptionalScopedRates(
                    pointEconomyEl, "skillPointsPerThetaMilliByScope", "pointEconomy"));

            var guardEconomyEl = Obj(root, "guardEconomy", "$");
            var guardEconomy = new AptitudeGuardEconomy(
                FlatCommitCost: PositiveMilli(guardEconomyEl, "flatCommitCost", "guardEconomy"),
                AbsorbDrainSharePermille: PositiveMilli(guardEconomyEl, "absorbDrainSharePermille", "guardEconomy"),
                RiposteShareCapPermille: PositiveMilli(guardEconomyEl, "riposteShareCapPermille", "guardEconomy"));

            var mitigationEl = Obj(root, "mitigation", "$");
            var mitigationFamilies = StringArray(mitigationEl, "families", "mitigation");
            if (mitigationFamilies.Count == 0)
                throw new AptitudeTuningRejection("aptitude tuning: missing required key 'mitigation.families'");
            var mitigation = new AptitudeMitigation(
                ScaleMilli: NonNegativeMilli(mitigationEl, "scaleMilli", "mitigation"),
                Families: mitigationFamilies);

            if (!root.TryGetProperty("edges", out var edgesEl) || edgesEl.ValueKind != JsonValueKind.Array)
                throw new AptitudeTuningRejection("aptitude tuning: missing required key 'edges'");

            var edges = new List<AptitudeEdge>();
            var droppedRetiredEdges = 0;
            var tuningSoFar = new AptitudeTuning(schemaVersion, version, grant, read, recovery, familyRead, edges, pointEconomy, guardEconomy, mitigation);
            foreach (var edgeEl in edgesEl.EnumerateArray())
            {
                // A `_group` divider carries no channel and is dropped — the edge list is long enough
                // that reading it without section headings is genuinely harder, and JSON has no
                // comment a publisher tool would preserve.
                if (!edgeEl.TryGetProperty("channel", out var channelEl) || channelEl.ValueKind != JsonValueKind.String)
                    continue;
                var channel = channelEl.GetString()!;
                if (string.IsNullOrWhiteSpace(channel)) continue;

                // `retire-atk` R2 — one filter, here, for every caller. A retired channel's edge is
                // dropped and counted BEFORE the `familyRead` lookup, because a retired channel no
                // longer has a `familyRead` row (the key is ignored above) and the lookup would
                // otherwise reject an archived file that is still supposed to load. This is what keeps
                // `aptitudes.v1`–`v8` loadable, including `aptitudes.v2` — the pin `CreatureSpeciesGen`
                // still bakes species from.
                if (DerivedStatChannels.Retired.Contains(channel))
                {
                    droppedRetiredEdges++;
                    continue;
                }

                var source = edgeEl.TryGetProperty("source", out var sourceEl) ? sourceEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(source))
                    throw new AptitudeTuningRejection($"aptitude tuning: edge for channel '{channel}' has no source");

                var family = tuningSoFar.FamilyOf(channel);
                if (family is null)
                    throw new AptitudeTuningRejection(
                        $"aptitude tuning: familyRead has no entry for '{channel}' — the read mode is a property of the channel and cannot be inferred");

                var kMilli = Int64(edgeEl, "kMilli", $"edge '{channel}'");
                edges.Add(new AptitudeEdge(channel, source!, kMilli, familyRead[family]));
            }
            if (edges.Count == 0)
                throw new AptitudeTuningRejection("aptitude tuning: missing required key 'edges' (empty)");

            return tuningSoFar with { DroppedRetiredEdges = droppedRetiredEdges };
        }
    }

    static JsonElement Obj(JsonElement parent, string key, string parentPath)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Object)
            throw new AptitudeTuningRejection($"aptitude tuning: missing required key '{(parentPath == "$" ? key : parentPath + "." + key)}'");
        return el;
    }

    /// <summary>The four-scope rate table backing <see cref="AptitudePointEconomy.SkillPointsPerThetaMilliByScope"/>
    /// — C6, spec-tree-state.md §3 (D34). Unlike <see cref="Obj"/>, an ABSENT key is not a rejection:
    /// a tuning file that never carries `key` parses to an empty table (never a guessed rate), and
    /// <see cref="PointBudget.SkillPointsFor"/> is what turns "no rate for this scope" into a load
    /// rejection, at first use. But once the key IS present it is exactly as strict as the sibling
    /// table `Obj`+<see cref="PositiveMilli"/> already enforce: all four scopes required, each a named
    /// rejection if absent, so a half-authored table can never ship silently.</summary>
    static IReadOnlyDictionary<AllocationScope, long> OptionalScopedRates(JsonElement parent, string key, string parentPath)
    {
        if (!parent.TryGetProperty(key, out var el))
            return new Dictionary<AllocationScope, long>();
        if (el.ValueKind != JsonValueKind.Object)
            throw new AptitudeTuningRejection($"aptitude tuning: '{parentPath}.{key}' must be an object");

        var path = parentPath + "." + key;
        return new Dictionary<AllocationScope, long>
        {
            [AllocationScope.Commander] = PositiveMilli(el, "commander", path),
            [AllocationScope.CreatureType] = PositiveMilli(el, "creatureType", path),
            [AllocationScope.Aspect] = PositiveMilli(el, "aspect", path),
            [AllocationScope.UniqueCreature] = PositiveMilli(el, "uniqueCreature", path),
        };
    }

    /// <summary>
    /// species-progression SP6.0 (R21) — <see cref="AptitudeLayerWeights"/>'s own parse. Absence is
    /// NOT a rejection (see that type's own doc comment: an archived pre-R21 file, e.g.
    /// `aptitudes.v2.json` still pinned by `gk-forge/tools/CreatureSpeciesGen`, must stay loadable). If the key
    /// IS present it is strict, by name: a missing scope key, an UNKNOWN scope key (unlike its sibling
    /// <see cref="OptionalScopedRates"/>, which silently ignores an extra key — this table must not),
    /// or a negative weight are each a named rejection.
    /// </summary>
    static AptitudeLayerWeights ParseLayerWeights(JsonElement readEl)
    {
        const string path = "read.layerWeightMilliByScope";
        if (!readEl.TryGetProperty("layerWeightMilliByScope", out var el))
            return new AptitudeLayerWeights(new Dictionary<AllocationScope, long>());
        if (el.ValueKind != JsonValueKind.Object)
            throw new AptitudeTuningRejection($"aptitude tuning: '{path}' must be an object");

        var known = new Dictionary<string, AllocationScope>(StringComparer.Ordinal)
        {
            ["commander"] = AllocationScope.Commander,
            ["creatureType"] = AllocationScope.CreatureType,
            ["aspect"] = AllocationScope.Aspect,
            ["uniqueCreature"] = AllocationScope.UniqueCreature,
        };
        var weights = new Dictionary<AllocationScope, long>();
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Name.StartsWith('_')) continue; // notes, never data
            if (!known.TryGetValue(prop.Name, out var scope))
                throw new AptitudeTuningRejection($"aptitude tuning: '{path}' has unknown scope key '{prop.Name}'");
            if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt64(out var w))
                throw new AptitudeTuningRejection($"aptitude tuning: '{path}.{prop.Name}' must be an integer");
            if (w < 0)
                throw new AptitudeTuningRejection($"aptitude tuning: '{path}.{prop.Name}' must not be negative — got {w}");
            weights[scope] = w;
        }
        foreach (var (name, scope) in known)
            if (!weights.ContainsKey(scope))
                throw new AptitudeTuningRejection($"aptitude tuning: '{path}' is missing required scope key '{name}'");

        return new AptitudeLayerWeights(weights);
    }

    static List<string> StringArray(JsonElement parent, string key, string parentPath)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Array)
            throw new AptitudeTuningRejection($"aptitude tuning: missing required key '{parentPath}.{key}'");
        return el.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => s.Length > 0).ToList();
    }

    static int Int(JsonElement parent, string key, string parentPath)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new AptitudeTuningRejection($"aptitude tuning: missing required key '{(parentPath == "$" ? key : parentPath + "." + key)}'");
        return el.GetInt32();
    }

    static long Int64(JsonElement parent, string key, string context)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new AptitudeTuningRejection($"aptitude tuning: {context} missing required key '{key}'");
        return el.GetInt64();
    }

    static long PositiveMilli(JsonElement parent, string key, string parentPath)
    {
        var v = Int64(parent, key, parentPath);
        if (v <= 0) throw new AptitudeTuningRejection($"aptitude tuning: '{parentPath}.{key}' must be positive — got {v}");
        return v;
    }

    static long NonNegativeMilli(JsonElement parent, string key, string parentPath)
    {
        var v = Int64(parent, key, parentPath);
        if (v < 0) throw new AptitudeTuningRejection($"aptitude tuning: '{parentPath}.{key}' must not be negative — got {v}");
        return v;
    }

    /// <summary>`spanPoints` ships as a JSON double (e.g. `100.0`) in the authored file, not an
    /// already-milli integer — converted once, here, at the parse boundary, exactly like every other
    /// value in this loader (never left as a live `double` for a consumer to re-derive).</summary>
    static long PositiveMilliFromDouble(JsonElement parent, string key, string parentPath)
    {
        if (!parent.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.Number)
            throw new AptitudeTuningRejection($"aptitude tuning: missing required key '{parentPath}.{key}'");
        var d = el.GetDouble();
        if (d <= 0) throw new AptitudeTuningRejection($"aptitude tuning: '{parentPath}.{key}' must be positive — got {d}");
        return checked((long)Math.Round(d * 1000.0, MidpointRounding.AwayFromZero));
    }
}
