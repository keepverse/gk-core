using System.Text.Json;
using FusionRpg.Core.Commanders;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Stats.Derived.Subsystems;

namespace FusionRpg.Core.Creatures.Layers;

/// <summary>
/// species-progression module 3 (spec-species-layer-projector.md) — the ONE projector mechanism
/// serving 1a (species-passive core), 1b (a ledger instance) and 2b (an empire species allocation),
/// rather than two passes over the same machinery (ideal R-S3). SP3.4 builds the 2b half
/// (<see cref="ProjectEmpire"/> + <see cref="Resolve"/>); SP3.5 adds 1a/1b (<c>ProjectBase</c>/
/// <c>ProjectPlayerMod</c>); SP3.7 adds <c>ToContainer</c>.
///
/// <para><b>The invariant that makes 2b honest</b> (spec, same heading): for every species-only
/// allocation `A` (only <see cref="AllocationScope.CreatureType"/> points) and every `Θ`,
/// <c>Resolve(ProjectEmpire(A), P(Θ))</c> equals <c>AptitudeResolver.Resolve(A, tuning, ladder, Θ,
/// registry)</c> channel for channel, op for op, value for value — only the SourceId differs, by
/// design. A merged allocation (commander + species) is never an input here: module 5 hands this
/// the species term alone (owner ruling R2, 2026-09-18).</para>
/// </summary>
public static class SpeciesLayerProjector
{
    /// <summary>
    /// Bumped only when the projection's arithmetic changes (e.g. ladder-scale-parity). Part of
    /// module 5's input digest so a stored container can never outlive the arithmetic that produced
    /// it. Structural, not tunable: it versions code, not balance.
    /// </summary>
    public const int Revision = 1;

    /// <summary>
    /// 2b: one row per funded aptitude edge in a species-only allocation. Never reads 1a or 1b — no
    /// composed value, an allocation and tuning are the only inputs (spec rule 4).
    /// </summary>
    public static IReadOnlyList<ProjectedLayerRow> ProjectEmpire(
        EmpireId empire, string speciesId, AptitudeAllocation allocation,
        AptitudeTuning tuning, DerivedStatRegistry registry)
    {
        if (allocation is null) throw new ArgumentNullException(nameof(allocation));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        if (registry is null) throw new ArgumentNullException(nameof(registry));

        var rows = new List<ProjectedLayerRow>();
        foreach (var edge in tuning.Edges)
        {
            // A 2b allocation holds one scope (CreatureType), so its share within that scope is the
            // same number species-layer-delivery step 6.1's per-scope resolver computes.
            var share = allocation.ShareWithinScope(AllocationScope.CreatureType, edge.Source);
            if (share <= 0.0) continue;                               // same skip as AptitudeResolver.cs:36-37
            if (!registry.TryResolveChannel(edge.Channel, out var def))
                throw new InvalidOperationException($"aptitude edge targets unregistered channel '{edge.Channel}'");
            var kMilli = AptitudeResolver.EffectiveKMilli(tuning, edge);   // the SAME coefficient rule, made internal-visible, not copied
            var op = def.Compose == DerivedComposeKind.SumIncreased ? DerivedModifierOp.Increased : DerivedModifierOp.Flat;
            LayerValue value = edge.Mode == AptitudeReadMode.Contest
                ? new LayerValue.Fixed(AptitudeReadFunctions.Contest(kMilli, share,
                      tuning.Read.Contest.ShareExponentMilli, tuning.Read.Contest.SpanPointsMilli))
                : new LayerValue.LadderMicro(checked(kMilli * AptitudeReadFunctions.SharePowMilli(
                      share, tuning.Read.Magnitude.ShareExponentMilli)));
            rows.Add(new ProjectedLayerRow(edge.Channel, op, value,
                ContributionSourceIds.SpeciesEmpire(empire, speciesId, edge.Source)));
        }
        return rows;
    }

    /// <summary>Resolves projected rows against a reader's own `P(Θ)` — the SourceId travels
    /// unchanged, so the reader can tell exactly which producer minted each modifier.</summary>
    public static IReadOnlyList<DerivedModifier> Resolve(IReadOnlyList<ProjectedLayerRow> rows, long pTheta)
    {
        if (rows is null) throw new ArgumentNullException(nameof(rows));
        return rows.Select(r => new DerivedModifier(r.Channel, r.Op, r.Value switch
        {
            LayerValue.Fixed f => f.Amount,
            LayerValue.LadderMicro l => Core.Power.LadderScale.Micro(l.KMicro, pTheta),
            _ => throw new ArgumentOutOfRangeException(nameof(rows)),
        }, SourceId: r.SourceId)).ToList();
    }

    /// <summary>
    /// 1a: the generator's <c>species-passive.{speciesId}</c> template's own fixed core — its
    /// <c>stat.derived</c> atoms only, at their DEFINITION values (never rolled, matching T4.6's
    /// "definition values" contract). <paramref name="resolveAtom"/> supplies the atom definition; a
    /// null answer skips the row rather than guessing (same discipline the equip parse applies to an
    /// unreadable amount).
    /// </summary>
    public static IReadOnlyList<ProjectedLayerRow> ProjectBase(
        string speciesId, ContainerRow template, Func<string, AtomRow?> resolveAtom)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (resolveAtom is null) throw new ArgumentNullException(nameof(resolveAtom));

        var sourceId = ContributionSourceIds.SpeciesBase(speciesId);
        var rows = new List<ProjectedLayerRow>();
        foreach (var entry in template.Atoms)
        {
            var def = resolveAtom(entry.AtomId);
            if (def is null) continue;
            if (!string.Equals(def.KindId, "stat.derived", StringComparison.Ordinal)) continue;

            // E2's own override layer, read first, then the definition -- the SAME "instance/override
            // first, definition second" discipline ProjectPlayerMod applies below, generalised to 1a's
            // own (currently unused in shipped content) override slot rather than silently ignoring it.
            var overrides = SafeReadJson(entry.OverridesJson);
            var pars = SafeReadJson(def.ParamsJson);
            if (!TryChannelOpAmount(overrides, pars, out var channel, out var op, out var amount)) continue;

            rows.Add(new ProjectedLayerRow(channel, op, new LayerValue.Fixed(amount), sourceId));
        }
        return rows;
    }

    /// <summary>
    /// 1b: one ledger instance's non-core atoms (pool rolls, forced picks) — every row whose
    /// <see cref="InstanceAtomRow.Seq"/> is NOT one of the template's own core seqs (rule 3: "1b never
    /// repeats 1a" — the instantiator numbers rolled rows strictly after the core's highest seq,
    /// <c>Instantiator.cs:120-143</c>). Instance <c>values_json</c> is read before the atom's
    /// definition <c>params_json</c> (T4.6's "instance values first" rule, carried over unchanged) —
    /// the roll is what the player owns, and a pick is expressed as a rolled value.
    /// </summary>
    public static IReadOnlyList<ProjectedLayerRow> ProjectPlayerMod(
        string speciesId, string mechanism, ContainerRow template, InstanceRow instance,
        Func<string, AtomRow?> resolveAtom)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        if (instance is null) throw new ArgumentNullException(nameof(instance));
        if (resolveAtom is null) throw new ArgumentNullException(nameof(resolveAtom));

        var coreSeqs = template.Atoms.Select(a => a.Seq).ToHashSet();
        var sourceId = ContributionSourceIds.SpeciesPlayer(speciesId, mechanism);
        var rows = new List<ProjectedLayerRow>();
        foreach (var row in instance.Atoms)
        {
            if (coreSeqs.Contains(row.Seq)) continue; // 1a's own seq -- ProjectBase already emits it

            var def = resolveAtom(row.AtomId);
            if (def is null) continue;
            if (!string.Equals(def.KindId, "stat.derived", StringComparison.Ordinal)) continue;

            var values = SafeReadJson(row.ValuesJson);
            var pars = SafeReadJson(def.ParamsJson);
            if (!TryChannelOpAmount(values, pars, out var channel, out var op, out var amount)) continue;

            rows.Add(new ProjectedLayerRow(channel, op, new LayerValue.Fixed(amount), sourceId));
        }
        return rows;
    }

    // ---- 1a/1b shared parse (T4.6's own rules, carried over unchanged) -----------------------------

    static IReadOnlyDictionary<string, JsonElement> SafeReadJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try { return Effects.Atoms.Power.CostFunction.Read(json); }
        catch (JsonException) { return new Dictionary<string, JsonElement>(StringComparer.Ordinal); }
    }

    /// <summary>Channel is required; a ValueSpec-object amount, an unreadable amount, or an unknown op
    /// (<see cref="AtomDerivedSubsystem.TryParseOp"/>) are each a SKIP, never a coercion.</summary>
    static bool TryChannelOpAmount(
        IReadOnlyDictionary<string, JsonElement> values, IReadOnlyDictionary<string, JsonElement> pars,
        out string channel, out DerivedModifierOp op, out double amount)
    {
        op = default;
        amount = 0;
        if (!TryString(values, pars, "channel", out channel)) return false;
        if (!TryAmount(values, pars, out var longAmount)) return false;
        amount = longAmount;

        var opText = TryString(values, pars, "op", out var opStr) ? opStr : null;
        return AtomDerivedSubsystem.TryParseOp(opText, out op);
    }

    static bool TryString(
        IReadOnlyDictionary<string, JsonElement> values, IReadOnlyDictionary<string, JsonElement> pars,
        string key, out string result)
    {
        foreach (var source in new[] { values, pars })
        {
            if (source.TryGetValue(key, out var el) && el.ValueKind == JsonValueKind.String)
            {
                result = el.GetString()!;
                return true;
            }
        }
        result = "";
        return false;
    }

    static bool TryAmount(
        IReadOnlyDictionary<string, JsonElement> values, IReadOnlyDictionary<string, JsonElement> pars,
        out long amount)
    {
        foreach (var source in new[] { values, pars })
        {
            // `amount` is ParamKind.Value: a plain number OR a ValueSpec OBJECT (a curve reference).
            // Guarding the kind is not optional -- JsonElement.TryGetInt64 THROWS on an object rather
            // than returning false. No ValueSpec resolver here either, so an object is skipped, not
            // coerced (rule 2).
            if (source.TryGetValue("amount", out var el)
                && el.ValueKind == JsonValueKind.Number
                && el.TryGetInt64(out amount))
                return true;
        }
        amount = 0;
        return false;
    }

    // ---- 5: ToContainer -----------------------------------------------------------------------------

    /// <summary>
    /// species-progression SP3.7 — builds a persistable <see cref="ContainerKind.SpeciesProgression"/>
    /// container from a set of projected rows (from any of <see cref="ProjectBase"/>,
    /// <see cref="ProjectPlayerMod"/>, or <see cref="ProjectEmpire"/>). Pure Core, no I/O: the returned
    /// atoms and container are values a Data-layer caller upserts through its own store, the same split
    /// <c>BuildMagnitudeAtoms</c>/<c>SynthesizeMagnitudeContainerUnlocked</c> already establishes
    /// (<c>RpgStore.Species.cs</c>).
    ///
    /// <para>A <see cref="LayerValue.LadderMicro"/> value serialises as the existing ValueSpec
    /// <c>{"powerLadder": true, "kMicro": K}</c> (<c>AtomJson.cs:77-83</c>), so a persisted container is
    /// readable by every existing container tool with no new grammar; a <see cref="LayerValue.Fixed"/>
    /// value serialises as a literal <c>amount</c>.</para>
    ///
    /// <para><paramref name="containerId"/> is caller-supplied, not derived here — the container-id
    /// scheme for a 2b (per empire+species) vs. a 1a/1b (per species) carrier is
    /// `solid-remediation/spec-species-carrier.md:150`'s own open line (GG-49 `species-passive:`
    /// retired), handed to that session or recorded under this program's Checkpoint 1, not decided by
    /// this pure builder.</para>
    ///
    /// <para><b>Known limitation, named rather than papered over:</b> <see cref="AtomJson.TryReadValueSpec"/>
    /// (the real grammar every other atom's `amount` field parses through) requires a plain-number
    /// amount to be a 32-bit integer (`el.TryGetInt32`) — it never accepts a fraction. 1a/1b's own
    /// `Fixed` values are always whole numbers by construction (sourced from a `long` definition
    /// amount), so this holds for them. A <see cref="ProjectEmpire"/> Contest-mode row's `Fixed` value
    /// is a genuine `double` (`AptitudeReadFunctions.Contest`) and MAY carry a fraction; feeding one to
    /// this builder produces an atom whose `amount` a real `AtomJson` parse would reject. No caller in
    /// this program does that today (2b's contest edges are consumed as `DerivedModifier`s via
    /// <see cref="Resolve"/>, never persisted through this method), so it is recorded here rather than
    /// guessed at with a premature rounding rule nobody asked for.</para>
    /// </summary>
    public static (ContainerRow Container, IReadOnlyList<AtomRow> Atoms) ToContainer(
        string containerId, IReadOnlyList<ProjectedLayerRow> rows)
    {
        if (string.IsNullOrWhiteSpace(containerId))
            throw new ArgumentException("container id is required", nameof(containerId));
        if (rows is null) throw new ArgumentNullException(nameof(rows));

        var family = $"atom.{SyntheticStatDerivedAtoms.Kebab(containerId)}";
        var atoms = new List<AtomRow>();
        var members = new List<ContainerAtomRow>();

        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var seq = i + 1;
            // The channel alone is not a safe disambiguator: unlike BuildMagnitudeAtoms's dictionary
            // input, two rows here (e.g. two aptitude edges funding the same channel) CAN legitimately
            // share a channel -- the row's own position keeps every atom id distinct regardless.
            var variant = $"{SyntheticStatDerivedAtoms.Kebab(row.Channel)}-{seq}";
            var opText = row.Op switch
            {
                DerivedModifierOp.Flat => "flat",
                DerivedModifierOp.Increased => "increased",
                DerivedModifierOp.Replace => "replace",
                DerivedModifierOp.Flag => "flag",
                _ => throw new ArgumentOutOfRangeException(nameof(rows), row.Op, "unknown DerivedModifierOp"),
            };
            var amountJson = row.Value switch
            {
                LayerValue.Fixed f => JsonSerializer.Serialize(f.Amount),
                LayerValue.LadderMicro l => "{\"powerLadder\":true,\"kMicro\":" + l.KMicro.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}",
                _ => throw new ArgumentOutOfRangeException(nameof(rows)),
            };

            var atomId = AtomRow.DeriveId(family, variant, 1);
            atoms.Add(new AtomRow
            {
                AtomId = atomId,
                KindId = "stat.derived",
                FamilyId = family,
                Variant = variant,
                Tier = 1,
                Name = family,
                ParamsJson = "{\"channel\":" + JsonSerializer.Serialize(row.Channel)
                    + ",\"op\":\"" + opText + "\",\"amount\":" + amountJson + "}",
                WhenJson = "{}",
            });
            members.Add(new ContainerAtomRow(seq, atomId));
        }

        var container = new ContainerRow
        {
            ContainerId = containerId,
            Kind = ContainerKind.SpeciesProgression,
            Atoms = members,
        };
        return (container, atoms);
    }
}
