using System.Text.Json.Nodes;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Effects.Atoms.Generation;

/// <summary>
/// E43 <c>family-expand</c> (spec-family-expand.md): turns the authored affix families into atom rows,
/// one row per (family, tier) — the tier axis is the only one that still materialises (§3.2, decided
/// 2026-09-03). Element does not materialise: a <c>{variant}</c>-templated channel emits ONE row per
/// tier carrying an E30 pool reference, never seven rows differing by concrete element (W7.9).
///
/// <para><b>Pure.</b> No file I/O, no database, no static config reads beyond the two delegates the
/// caller supplies. The same inputs always produce the same rows in the same order — the whole basis
/// for the CLI's <c>--check</c> mode and for this module's own determinism test.</para>
///
/// <para><b>Magnitudes come from <c>bands.v1.json</c>'s <c>channelFamilyGroups.primaryChannel</c>
/// formula, ported from <c>gk-forge/tools/seedsmith/seedsmith/numerics/formulas.py</c></b> — same function
/// shapes, same plain-integer <c>round_legible</c> (the registry's own richer 1/2/5-significance snap
/// is a documented, bounded gap neither this module nor its Python sibling closes). Every intermediate
/// is <c>long</c>, widened before multiplying, divided by 1000 last, and overflow throws — this
/// module's own binding numeric rule (docs/architecture/numeric-types.md), not a style choice.</para>
/// </summary>
public static class FamilyExpansion
{
    // Structural (tunables-ssot.md T2) — bands.v1.json's own bandCount/bandCountRationale: "5, one per
    // tier the atom layer already has... a sixth powerBand would need a sixth .t6 row on EVERY family
    // — an atom-layer change, not a bands-registry one." Not a dial a balance pass turns.
    public const int TierCount = 5;

    // Structural — bands.v1.json powerBand.tierScaling.referenceLevel. A balance pass moves
    // sharePermille in tier-bands.v1.json, never the level a reference curve is read at.
    public const int ReferenceLevel = 20;

    // Structural — bands.v1.json powerBand.tierScaling, frozen with the registry.
    const int MagnitudeRatioPermille = 1750;
    const int BandFloorPermille = 670;
    const int BandCeilingPermille = 1330;

    // Structural — bands.v1.json primaryChannel.unit: "Increased or More -> integer per-mille (a
    // ratio against the identity 1000‰)". Not a share, not a curve value — the ratio's own identity.
    const int IdentityPermille = 1000;

    /// <summary>
    /// E30's shipped pools (spec-channel-pool.md), keyed by the exact <c>{variant}</c>-templated
    /// channel an authored family names. Deliberately NOT a guessed/inferred mapping — a template with
    /// no entry here (e.g. <c>combat.power.pierce.{variant}</c>, <c>combat.power.overflow.{variant}</c>)
    /// has no shipped pool and is refused by name, never assigned the nearest-looking one.
    /// </summary>
    static readonly IReadOnlyDictionary<string, string> VariantChannelToPool =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["combat.power.{variant}"] = "pool.element-power",
            ["combat.defense.{variant}"] = "pool.element-defense",
            ["combat.accuracy.{variant}"] = "pool.element-accuracy",
            ["combat.dodge.{variant}"] = "pool.element-dodge",
            ["combat.crit.rate.{variant}"] = "pool.element-crit-rate",
            ["combat.crit.resist.{variant}"] = "pool.element-crit-resist",
            ["combat.crit.damage.{variant}"] = "pool.element-crit-damage",
            ["combat.crit.resist.damage.{variant}"] = "pool.element-crit-resist-damage",
            ["combat.shield.capacity.{variant}"] = "pool.element-shield-capacity",
            ["combat.shield.toughness.{variant}"] = "pool.element-shield-toughness",
            ["combat.shield.pen.{variant}"] = "pool.element-shield-pen",
            ["combat.shield.regen.{variant}"] = "pool.element-shield-regen",
        };

    /// <summary>
    /// <c>formulas.py</c>'s own <c>round_legible</c>, ported as long-safe integer round-half-up:
    /// <c>(numerator + denominator/2) / denominator</c>. Proven against the two committed worked
    /// examples in <c>bands.v1.json</c> (vitality 30‰×680/1000=20.4→20, might 45‰×92/1000=4.14→4).
    /// This is plain rounding, not the registry's richer 1/2/5-significance snap — a documented,
    /// bounded gap, not a silent approximation (see the Python sibling's own docstring).
    /// </summary>
    public static long RoundLegible(long numerator, long denominator)
    {
        if (denominator <= 0) throw new ArgumentOutOfRangeException(nameof(denominator));
        checked { return (numerator + denominator / 2) / denominator; }
    }

    /// <summary><c>m_t = round_legible(m1 × ratio^(t-1) / 1000^(t-1))</c>, one multiply-divide step at
    /// a time so no intermediate needs more than one extra factor of <paramref name="ratioPermille"/>
    /// headroom over <paramref name="m1"/> itself.</summary>
    public static IReadOnlyList<long> TierLadder(long m1, int tierCount, int ratioPermille)
    {
        var ladder = new List<long>(tierCount) { m1 };
        checked
        {
            for (var t = 2; t <= tierCount; t++)
                ladder.Add(RoundLegible(ladder[^1] * ratioPermille, 1000));
        }
        return ladder;
    }

    /// <summary><c>(lo_t, hi_t)</c> — the ±33% band around a tier midpoint (bands.v1.json's own
    /// default width; <c>variance</c> overrides are an authoring-time concern this generator does not
    /// touch — no family in this corpus authors one).</summary>
    public static (long Lo, long Hi) Band(long midTier) => checked((
        RoundLegible((long)BandFloorPermille * midTier, 1000),
        RoundLegible((long)BandCeilingPermille * midTier, 1000)));

    /// <summary>
    /// True when <paramref name="kindId"/>'s own <c>op</c> param is a stat modifier
    /// (<c>Flat</c>/<c>Increased</c>/<c>More</c>/...), rather than a kind-specific verb.
    /// Only <c>stat.modify</c> (FA1: "Ops are Flat|Increased|More") and <c>stat.derived</c>
    /// own one — every other kind's <c>op</c> is its own opcode: <c>board.action</c>'s
    /// <c>freeze|doom|fireline|cherry</c>, <c>resource.economy</c>'s <c>add|+|set</c>
    /// (<c>AtomKindRegistry.BoardActionOps/EconomyOps</c>). Reading a verb as a modifier
    /// produced the misleading <c>no opWeightPermille entry for op 'cherry'</c> refusal (R9):
    /// a verb is carried through unchanged and never looked up in the modifier table.
    /// </summary>
    public static bool OwnsModifierOp(string kindId) =>
        kindId is "stat.modify" or "stat.derived";

    /// <summary>
    /// Expand every family. <paramref name="flatReferenceBaseGameUnits"/> resolves a concrete channel
    /// to its <c>BattleRuleset</c> curve value at <see cref="ReferenceLevel"/> — <c>null</c> when no
    /// shipped curve exists for that channel (e.g. <c>arm1Max</c>/<c>arm2Max</c>/<c>attackInterval</c>
    /// today), in which case a Flat-op family on that channel is refused rather than guessing a base.
    /// Called only for <c>Flat</c> ops — <c>Increased</c>/<c>More</c> use the identity ratio and never
    /// touch a game curve at all (bands.v1.json's own unit note). A family whose kind does not own a
    /// modifier op (<see cref="OwnsModifierOp"/>) resolves its magnitude the same way as the Flat
    /// sub-case by the registry's own analogy (<c>bands.v1.json</c>
    /// <c>powerBand.familiesOutOfFourWaySplit</c>), with its verb carried verbatim.
    /// A <c>status.apply</c> family resolves from <paramref name="statusAnchorFor"/>
    /// (null = no anchor wired: such families refuse naming the missing anchor row).
    /// </summary>
    public static FamilyExpansionResult Expand(
        IReadOnlyList<FamilyEntryInput> families,
        TierBandsInput tierBands,
        Func<string, long?> flatReferenceBaseGameUnits,
        Func<string, StatusAnchorRow?>? statusAnchorFor = null)
    {
        var rows = new List<AtomRow>();
        var refusals = new List<FamilyRefusal>();
        var seenKeys = new HashSet<(string Family, int Tier, string Variant)>();

        foreach (var family in families)
        {
            var stem = StemOf(family.Id);

            if (!tierBands.ChannelWeightPermille.TryGetValue(stem, out var channelWeight))
            {
                refusals.Add(new FamilyRefusal(family.Id,
                    $"no authored sharePermille for family '{family.Id}' (channel stem '{stem}' not in " +
                    "the loaded tier-bands input -- the host reads the LATEST published revision, " +
                    "tier-bands.v{n}.json, via TierBandsFile.FindLatestPath)"));
                continue;
            }

            // R9: a modifier-op weight applies only to kinds that own a modifier op
            // (stat.modify/stat.derived). Every other kind's `op` is its own verb
            // (board.action's cherry, resource.economy's add) — looking a verb up in the
            // modifier table refused real families with a misleading reason. A verb carries
            // no share distinction, so it keeps the identity weight, exactly like an op-less
            // family, and is validated (if at all) by the atom layer's own ParamSchema, never here.
            long opWeight = IdentityPermille;
            if (OwnsModifierOp(family.KindId) && !string.IsNullOrEmpty(family.Op))
            {
                if (!tierBands.OpWeightPermille.TryGetValue(family.Op, out var w))
                {
                    // EX-2 (ladder principle): Replace/Flag get the principled message, not the
                    // table-miss message — a constant has no place in f(Theta) (parity section 2,
                    // PS-3, section 5.1), so the refusal teaches the verdict instead of implying a
                    // missing table row. Any other unknown modifier op is still a table miss.
                    var reason = family.Op is "Replace" or "Flag"
                        ? $"op '{family.Op}' is excluded by principle on family '{family.Id}' — " +
                          "Replace/Flag substitute a constant where the power ladder requires f(Theta) " +
                          "(ssot-power-scale.md sections 2/5.1, PS-3); re-author as Flat to price it"
                        : $"no opWeightPermille entry for op '{family.Op}' on family '{family.Id}'";
                    refusals.Add(new FamilyRefusal(family.Id, reason));
                    continue;
                }
                opWeight = w;
            }

            // sharePermille(family) = baseSharePermille × channelWeightPermille[stem] ×
            // opWeightPermille[op] / 1_000_000 — trusts the shipped DATA FILE's uniform-35‰ shape
            // over ssot-affixes.md §4.5's illustrative-only worked table (spec §3.2 step 1: the doc's
            // own _meta flags those examples "illustrative, not balanced", while tier-bands.v1.json's
            // _meta calls itself "the one genuinely tunable surface of item balance").
            long sharePermille;
            checked { sharePermille = RoundLegible(tierBands.BaseSharePermille * channelWeight * opWeight, 1_000_000); }

            var isElementTyped = family.Channel.Contains("{variant}", StringComparison.Ordinal);
            string? poolId = null;
            if (isElementTyped)
            {
                if (!VariantChannelToPool.TryGetValue(family.Channel, out poolId))
                {
                    refusals.Add(new FamilyRefusal(family.Id,
                        $"no matching E30 channel pool for '{family.Channel}' — none of the 12 shipped pools " +
                        "(data/seed/channel-pools/pools.v1.json) has a member set for this channel family"));
                    continue;
                }
            }

            // A bare `status.*` family stem is a catalog FAMILY TEMPLATE, not a concrete channel: the six
            // status families all carry `expand: status-category`, so the registered vocabulary holds
            // `status.resist.omni`/`.dot`/`.cc`/`.contagion`, never the bare `status.resist`. Emitting the
            // stem produced a row AtomRowValidator refuses as an illegal channel (E29's closed
            // vocabulary) — found live on `atom.ward-ward`, whose `Increased` op slipped past both the
            // pool check (no `{variant}`) and the Flat-only reference-base check. Refuse it here by name,
            // the same disposition `atom.stalwart`/`atom.immunity`/`atom.affliction` already get, rather
            // than emit a row that cannot validate.
            if (family.KindId == "stat.derived" && IsBareStatusFamilyStem(family.Channel))
            {
                refusals.Add(new FamilyRefusal(family.Id,
                    $"channel '{family.Channel}' is a bare status family template (expand: status-category) — " +
                    "the concrete segment must be authored (`status.<family>.<category>`)"));
                continue;
            }

            // A scalar `stat.modify` channel outside `StatChannels.All` would emit a row
            // AtomRowValidator refuses as BadParamValue (E29's closed vocabulary) — found live on
            // `atom.enhance-quicken` (`actionSpeed`) and `atom.enhance-swift` (`plantMoveSpeed`), whose
            // `Increased` ops slipped past the Flat-only reference-base check the same way
            // `atom.ward-ward` once slipped past both checks. Refuse here with the vocabulary gap
            // named: widening `StatChannels.All` is the atom program's own SSOT decision (its registry
            // reads the list live, never a copy), not something a generator takes by emitting first.
            if (family.KindId == "stat.modify" && !isElementTyped
                && !AtomKindRegistry.PrimaryChannels.Contains(family.Channel, StringComparer.Ordinal))
            {
                refusals.Add(new FamilyRefusal(family.Id,
                    $"channel '{family.Channel}' is not one of stat.modify's {AtomKindRegistry.PrimaryChannels.Length} " +
                    "legal channels (Stats.StatChannels.All) — widening that vocabulary is the atom program's call"));
                continue;
            }

            // ⚠️ Merged 2026-09-16: species-gear-chain added the refusal guard above while another
            // branch extended this call with `statusAnchorFor`/`anchorRow` (status-apply families read
            // the anchor's duration and chance ladders below). Both survive — the guard runs first and
            // the call keeps the wider signature, because `anchorRow` is load-bearing downstream.
            if (!TryReferenceBaseM1(family, sharePermille, flatReferenceBaseGameUnits, statusAnchorFor,
                out var m1, out var anchorRow, out var refuseReason))
            {
                refusals.Add(new FamilyRefusal(family.Id, refuseReason!));
                continue;
            }

            var isStatusApply = anchorRow is not null;

            // family-tags-closure (D28, 2026-09-07): a family's own authored tag word becomes a bare
            // TagsJson key (EligibilityRule.RequireTags/ContainsKey never inspects the value) — refuse
            // rather than silently shadow the two provenance keys every row also carries.
            var reservedTagCollision = family.Tags.FirstOrDefault(
                t => t == "generatedFrom" || t == "generator");
            if (reservedTagCollision is not null)
            {
                refusals.Add(new FamilyRefusal(family.Id,
                    $"tag '{reservedTagCollision}' collides with a reserved provenance key " +
                    "(generatedFrom/generator) — rename the authored tag, it is never a family's own to use"));
                continue;
            }

            // status.apply rows price duration on the authored duration ladder and chance on the
            // authored chance ladder (ratios ride the anchor row, never code consts — audit M2).
            // Every other row prices amount on the magnitude ladder inside a ±33% band.
            var ladder = isStatusApply
                ? TierLadder(m1, TierCount, checked((int)anchorRow!.DurationRatioPermille))
                : TierLadder(m1, TierCount, MagnitudeRatioPermille);
            var chanceLadder = isStatusApply
                ? TierLadder(anchorRow!.ChanceT1Permille, TierCount, checked((int)anchorRow.ChanceRatioPermille))
                : null;
            // R9: a verb is carried verbatim — never lowercased/normalized. Normalizing here
            // would silently accept a misspelling the atom layer's own ParamSchema vocabulary
            // ("freeze|doom|fireline|cherry") is supposed to refuse by name. Modifier ops keep
            // the established lowercase emission byte-identical committed rows depend on.
            var opLower = OwnsModifierOp(family.KindId)
                ? (string.IsNullOrEmpty(family.Op) ? "flat" : family.Op.ToLowerInvariant())
                : (family.Op ?? "flat");

            for (var t = 1; t <= TierCount; t++)
            {
                // Element does not materialise (W7.9) — variant stays "" even for a pool-typed
                // channel, so the id grammar never grows a variant segment E43 did not earn.
                const string variant = "";
                var key = (family.Id, t, variant);
                if (!seenKeys.Add(key))
                {
                    refusals.Add(new FamilyRefusal(family.Id,
                        $"collision on (family_id, tier, variant) = ('{family.Id}', {t}, '{variant}')"));
                    continue;
                }

                var (lo, hi) = Band(ladder[t - 1]);

                // status.apply rows speak the status schema (status/duration/level, scalar duration
                // per the hand-authored fx-status.json precedent — no amount band, no op: the kind
                // owns no modifier op) with the grant chance riding in WhenJson (per-mille int, the
                // carrier AtomCompiler/AtomRunner/CostFunction already read). All other rows carry
                // channel/op/amount as before.
                JsonObject paramsObj;
                string whenJson = "{}";
                if (isStatusApply)
                {
                    // Duration is authored and laddered in ms (Milliseconds class, anchor file) and
                    // the status schema reads SECONDS — so the emitted row converts once, here.
                    // Emitting raw ms would apply 2000-second statuses: the exact silent-magnitude
                    // failure this pipeline exists to prevent.
                    //
                    // ⚠️ The conversion ROUNDS to a whole second. This used to emit `ms / 1000.0`,
                    // reasoning that the schema takes a float — it does not. `duration` is a
                    // ParamKind.Value, and AtomJson refuses a non-integer magnitude ("magnitudes are
                    // integers — see definitions §2 on units"). The hand-authored precedent this block
                    // cites, gk-data/packs/fusion/data/seed/atoms/fx-status.json, carries whole seconds (3, 4) and agrees.
                    // With the 1.40 duration ladder off a 2000 ms t1, every tier above t1 landed
                    // fractional (2.8, 3.92, 5.488, 7.683), so 56 of the 70 generated rows were
                    // rejected — and because a seed import is all-or-nothing, the whole tree failed to
                    // import. Floor of 1: a rounded-to-zero status would be a no-op row that still
                    // costs a slot.
                    var durationSeconds = Math.Max(1L, (long)Math.Round(ladder[t - 1] / 1000.0,
                        MidpointRounding.AwayFromZero));
                    paramsObj = new JsonObject
                    {
                        ["status"] = anchorRow!.StatusId,
                        ["duration"] = durationSeconds,
                        ["level"] = 1,
                    };
                    // status.apply REQUIRES a trigger (AtomRowValidator) — every generated row was
                    // rejected without one. The trigger is authored in the status-anchor registry, not
                    // chosen here: which event applies an affliction is content.
                    whenJson = new JsonObject
                    {
                        ["chance"] = chanceLadder![t - 1],
                        ["trigger"] = anchorRow.TriggerId,
                    }.ToJsonString();
                }
                else
                {
                    paramsObj = new JsonObject
                    {
                        ["channel"] = poolId is null
                            ? family.Channel
                            : new JsonObject { ["pool"] = poolId, ["count"] = 1, ["allowRepeat"] = false },
                        ["op"] = opLower,
                        ["amount"] = new JsonObject { ["min"] = lo, ["max"] = hi, ["roll"] = "onApply" },
                    };
                }

                var tagsObj = new JsonObject
                {
                    ["generatedFrom"] = family.SourceFile,
                    ["generator"] = "E43",
                };
                // Real family tags (family-tags-closure, D28): a bare word ("offensive") becomes a
                // key with a placeholder value, since EligibilityRule.RequireTags only checks
                // presence; a "key:value" word (none in real content today, but AnyOfTags's own shape
                // — see spec acceptance #6) splits on the first colon instead.
                foreach (var tag in family.Tags)
                {
                    var colon = tag.IndexOf(':');
                    if (colon < 0) tagsObj[tag] = "1";
                    else tagsObj[tag[..colon]] = tag[(colon + 1)..];
                }

                rows.Add(new AtomRow
                {
                    AtomId = AtomRow.DeriveId(family.Id, variant, t),
                    KindId = family.KindId,
                    FamilyId = family.Id,
                    Variant = variant,
                    Tier = t,
                    Name = $"{family.Name} T{t}",
                    WhenJson = whenJson,
                    ParamsJson = paramsObj.ToJsonString(),
                    TagsJson = tagsObj.ToJsonString(),
                    Enabled = true,
                });
            }
        }

        return new FamilyExpansionResult(rows, refusals);
    }

    static string StemOf(string familyId) =>
        familyId.StartsWith("atom.", StringComparison.Ordinal) ? familyId["atom.".Length..] : familyId;

    /// <summary>
    /// True when <paramref name="channel"/> is a bare <c>status.&lt;family&gt;</c> stem with no concrete
    /// category/id segment — the shape <c>derived-stat-catalog.v2.json</c> marks
    /// <c>expand: status-category</c>, whose real vocabulary members always carry a third segment
    /// (<c>status.resist.omni</c>). The three shipped families that author this shape
    /// (<c>atom.stalwart</c>/<c>atom.immunity</c>/<c>atom.affliction</c>) are already refused earlier for
    /// their own ops; this keeps any future bare-stem family from reaching the writer.
    /// </summary>
    static bool IsBareStatusFamilyStem(string channel)
    {
        if (!channel.StartsWith("status.", StringComparison.Ordinal) || channel.EndsWith('.')) return false;
        var rest = channel["status.".Length..];
        return !rest.Contains('.');
    }

    /// <summary>
    /// Duration and chance ladder ratios for <c>status.apply</c> rows live in the status anchor
    /// file, never here (audit M2: a balance number as a const). See
    /// <c>StatusAnchorFile</c> and the <c>statusAnchorFor</c> delegate this method already takes.
    /// </summary>
    /// <c>m1 = round_legible(sharePermille × referenceBase / 1000)</c> — <paramref name="referenceBase"/>
    /// is a real <c>BattleRuleset</c> curve value for a Flat op (game units), or the identity 1000‰ for
    /// Increased/More (bands.v1.json's own unit note: "Increased or More -> integer per-mille, a ratio
    /// against the identity 1000‰" — no game curve is read at all for those two ops). Any other op
    /// (<c>Replace</c>/<c>Flag</c>) carries no tier-band magnitude by the same document's own words and
    /// is refused rather than silently zeroed. A family whose kind owns no modifier op
    /// (<see cref="OwnsModifierOp"/> — its <c>op</c> is a verb like <c>cherry</c>, never a modifier)
    /// resolves by the registry's own analogy (<c>bands.v1.json</c>
    /// <c>powerBand.familiesOutOfFourWaySplit</c>): the Flat game-units sub-case, so a missing curve
    /// refuses naming the curve, never the verb.
    /// A <c>status.apply</c> family resolves from the status anchor instead
    /// (<paramref name="statusAnchorFor"/>): its m1 is the authored duration t1 in ms — durations do
    /// NOT take the share factor (share prices selection; the duration ladder prices growth), and the
    /// emitted chance ladder rides alongside. Data in, rows out: no dispatch, validation, or compose
    /// knowledge enters through this branch.
    /// </summary>
    static bool TryReferenceBaseM1(
        FamilyEntryInput family, long sharePermille, Func<string, long?> flatReferenceBaseGameUnits,
        Func<string, StatusAnchorRow?>? statusAnchorFor,
        out long m1, out StatusAnchorRow? anchorRow, out string? refuseReason)
    {
        m1 = 0;
        anchorRow = null;
        refuseReason = null;
        var op = family.Op ?? "";

        if (family.KindId == "status.apply")
        {
            var anchor = statusAnchorFor?.Invoke(family.Id);
            if (anchor is null)
            {
                refuseReason = $"no status-anchor row for family '{family.Id}' " +
                    "(status-anchor.v{n}.json familyStatus — the family-to-status mapping is authored, never inferred)";
                return false;
            }

            m1 = anchor.DurationT1Ms;
            anchorRow = anchor;
            return true;
        }

        if (!OwnsModifierOp(family.KindId))
        {
            var verbBase = flatReferenceBaseGameUnits(family.Channel);
            if (verbBase is null)
            {
                var opDesc = string.IsNullOrEmpty(op)
                    ? "carries no op"
                    : $"carries verb op '{op}', not a modifier";
                refuseReason = $"no referenceBaseGameUnits for channel '{family.Channel}' " +
                    $"(kind '{family.KindId}' {opDesc} — resolved by " +
                    "the Flat game-units analogy, bands.v1.json familiesOutOfFourWaySplit)";
                return false;
            }

            checked { m1 = RoundLegible(sharePermille * verbBase.Value, 1000); }
            return true;
        }

        if (string.Equals(op, "Flat", StringComparison.Ordinal))
        {
            var referenceBase = flatReferenceBaseGameUnits(family.Channel);
            if (referenceBase is null)
            {
                refuseReason = $"no referenceBaseGameUnits for channel '{family.Channel}' (op Flat) — " +
                                "no BattleRuleset curve is shipped for this channel yet";
                return false;
            }

            checked { m1 = RoundLegible(sharePermille * referenceBase.Value, 1000); }
            return true;
        }

        if (op is "Increased" or "More")
        {
            checked { m1 = RoundLegible(sharePermille * IdentityPermille, 1000); }
            return true;
        }

        refuseReason = $"op '{op}' has no supported tier-magnitude formula in E43's primaryChannel/" +
                        "flatDerivedChannel path — Replace/Flag carry no tier-band magnitude (bands.v1.json)";
        return false;
    }
}
