using System.Text.Json;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Items.Consumables;

/// <summary>
/// One core atom of a consumable, as the validator needs to see it: the atom row itself plus the
/// container's own override of its <c>when</c>, already merged by the caller. Kept as a tiny view
/// rather than taking <see cref="AtomRow"/> directly so a SEED (which has no atom row yet) and a
/// CONCRETE container (which does) can both be checked by the same rules.
/// </summary>
/// <param name="AtomId">For the message. Empty for a seed-derived view.</param>
/// <param name="KindId">The registry key. This is what makes the runtime check real.</param>
/// <param name="Tier">1..5. §5.2's band-consistency rule compares <c>grade</c> against every one.</param>
/// <param name="WhenJson">The merged <c>when</c> object — trigger, <c>chance</c>, <c>icd_ms</c>.</param>
public readonly record struct ConsumableCoreAtom(string AtomId, string KindId, int Tier, string WhenJson);

/// <summary>
/// The per-row import and catalog-load checks — ssot-consumables.md §6.1 and §6.3.
///
/// <para><b>Returns every failure rather than first-fail</b> (module 17's rule, kept): sixty rows
/// reported one problem at a time is sixty round trips.</para>
///
/// <para><b>No new member of the closed 33-code list.</b> §6.2 proposed four new codes; every rule
/// here is a namespaced <see cref="AtomRejectionReason.ContentRuleViolated"/> under
/// <see cref="ConsumableRules.Namespace"/> instead.</para>
/// </summary>
public static class ConsumableValidator
{
    /// <summary>
    /// Validate a <c>consumable_def</c> row against its container's core atoms.
    ///
    /// <para><paramref name="containerKind"/> is the container's declared kind. X7 landed
    /// 2026-09-07: <see cref="ContainerKind.Consumable"/> is real, so a bound kind that is anything
    /// ELSE is refused by name — a genuine content mistake to catch, not the "nothing can bind yet"
    /// blanket refusal this check used to be.</para>
    /// </summary>
    public static IReadOnlyList<AtomRejection> ValidateDef(
        ConsumableDefRow def,
        ContainerKind? containerKind,
        IReadOnlyList<ConsumableCoreAtom> coreAtoms,
        int prefixRolls,
        int suffixRolls,
        string? rarityId,
        int? minTier,
        int? maxTier,
        ConsumableTuning tuning)
    {
        if (def is null) throw new ArgumentNullException(nameof(def));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));
        coreAtoms ??= Array.Empty<ConsumableCoreAtom>();

        var fails = new List<AtomRejection>();

        if (!ConsumableContainerIds.IsWellFormed(def.ContainerId))
            fails.Add(ConsumableRules.Fail(ConsumableRules.Orphan,
                $"'{def.ContainerId}' is not a legal consumable container id — §4.6 fixes the prefix as " +
                $"'{ConsumableContainerIds.Prefix}'"));

        if (containerKind is not null && ConsumableLimits.ConsumableContainerKindAvailable
            && containerKind != ContainerKind.Consumable)
            fails.Add(ConsumableRules.Fail(ConsumableRules.ContainerKindUnavailable,
                $"'{def.ContainerId}' binds to a container of kind '{containerKind}', but a " +
                $"consumable_def must bind to a '{ContainerKind.Consumable}' container (X7, landed " +
                "2026-09-07) — the documented 'item' + 'slot IS NULL' fallback was withdrawn once the " +
                "real kind existed to bind to instead"));

        fails.AddRange(ValidateShape(def, coreAtoms, prefixRolls, suffixRolls, rarityId, minTier, maxTier, tuning));
        return fails;
    }

    /// <summary>
    /// The shape rules that hold for a seed and for a concrete container alike — everything except the
    /// container-kind binding.
    /// </summary>
    public static IReadOnlyList<AtomRejection> ValidateShape(
        ConsumableDefRow def,
        IReadOnlyList<ConsumableCoreAtom> coreAtoms,
        int prefixRolls,
        int suffixRolls,
        string? rarityId,
        int? minTier,
        int? maxTier,
        ConsumableTuning tuning)
    {
        var fails = new List<AtomRejection>();

        // ---- §6.2 code 1: a consumable does not roll -------------------------------------------------
        // "rarity IS affix count plus tier window" (§4.6), so a consumable with either has a rarity in
        // the only sense this tree uses the word. UnsatisfiablePool means the pool cannot be drawn from;
        // this is the opposite -- a pool that must not exist at all.
        if (prefixRolls != 0 || suffixRolls != 0)
            fails.Add(ConsumableRules.Fail(ConsumableRules.Rolls,
                $"'{def.ContainerId}' declares {prefixRolls} prefix / {suffixRolls} suffix rolls; a " +
                "consumable is destroyed by use, so it cannot roll and cannot stack if it did (§2)"));
        if (rarityId is not null)
            fails.Add(ConsumableRules.Fail(ConsumableRules.Rolls,
                $"'{def.ContainerId}' names rarity '{rarityId}'; consumables never enter the ladder — " +
                "their strength axis is the authored grade (§4.6)"));
        if (minTier is not null || maxTier is not null)
            fails.Add(ConsumableRules.Fail(ConsumableRules.Rolls,
                $"'{def.ContainerId}' declares a tier window; a container with no pool has nothing to " +
                "window"));

        // ---- §5.2 closed sets and their v1 subsets ---------------------------------------------------
        if (!tuning.Authors(def.ClassId))
            fails.Add(ConsumableRules.Fail(ConsumableRules.ClassUnavailable,
                $"'{def.ContainerId}' declares class '{ConsumableClasses.Wire(def.ClassId)}', which is " +
                "declared in the closed six but not authored in v1: each class names an EXECUTOR and " +
                "this one has none (§3.1). Widening data/tuning/consumables.v1.json's classesAuthored " +
                "is the whole change once the executor exists"));

        if (def.UseContexts.Count == 0)
            fails.Add(ConsumableRules.Fail(ConsumableRules.UseContextUnsupported,
                $"'{def.ContainerId}' names no use context, so it is usable nowhere"));

        foreach (var ctx in def.UseContexts)
            if (!tuning.Authors(ctx))
                fails.Add(ConsumableRules.Fail(ConsumableRules.UseContextUnsupported,
                    $"'{def.ContainerId}' names use context '{UseContexts.Wire(ctx)}', which the host " +
                    "cannot serve. 'battle' was authored 2026-09-05 once the action layer served it end " +
                    "to end (holdsStock reads the precondition, IStockLedger takes the stack at commit); " +
                    "'lawn' is the one still refused, and for its own reason — spec-usability-conditions.md " +
                    "§3a's mode matrix makes a holdsStock action NOT BINDABLE there (the overlay is a " +
                    "stateless observer, and ActionCompiler refuses it by name with " +
                    "ConsumableUnsupportedInMode), and capPerMatch (G4) is still unimplemented. Widening " +
                    "is additive and never invalidates a row (§4.1), so this is one line in " +
                    "consumables.v1.json the day the road exists"));

        // ---- §6.1 BadParamValue equivalents ----------------------------------------------------------
        if (def.Grade is < ConsumableLimits.MinGrade or > ConsumableLimits.MaxGrade)
            fails.Add(ConsumableRules.Fail(ConsumableRules.BadValue,
                $"'{def.ContainerId}' declares grade {def.Grade}, outside " +
                $"{ConsumableLimits.MinGrade}..{ConsumableLimits.MaxGrade} — the atom layer's five tiers"));

        if (def.ManifestCost < ConsumableLimits.MinManifestCost)
            fails.Add(ConsumableRules.Fail(ConsumableRules.BadValue,
                $"'{def.ContainerId}' declares manifest_cost {def.ManifestCost}; a consumable occupying " +
                "zero belt places makes the carry limit stop being a limit"));

        if (string.IsNullOrWhiteSpace(def.ExclusionGroup))
            fails.Add(ConsumableRules.Fail(ConsumableRules.BadValue,
                $"'{def.ContainerId}' has an empty exclusion_group; every consumable belongs to exactly " +
                "one one-per-run family (§4.4 defence 2)"));

        // ---- §6.3: grade equals the tier of EVERY core atom ------------------------------------------
        foreach (var atom in coreAtoms)
            if (atom.Tier != def.Grade)
                fails.Add(ConsumableRules.Fail(ConsumableRules.GradeMismatch,
                    $"'{def.ContainerId}' is grade {def.Grade} but core atom '{atom.AtomId}' is tier " +
                    $"{atom.Tier}; I3's band-consistency rule, borrowed — a mixed-tier core makes the " +
                    "grade a label rather than the strength axis"));

        foreach (var atom in coreAtoms)
        {
            fails.AddRange(ValidateAtom(def, atom));
            fails.AddRange(ValidateRuntimes(def, atom));
        }

        return fails;
    }

    /// <summary>
    /// ssot-consumables.md §3.3's live-runtime warning and §4.2's trigger rule, on one atom.
    ///
    /// <para><b>The trigger check asks the registry rather than carrying a list that can drift.</b>
    /// spec-consumables.md's Code-style block is followed verbatim in intent: <c>OnActivate</c> is the
    /// name (there is no <c>OnUse</c> and there must not be a second name for one concept), and which
    /// kinds carry it is read from <see cref="AtomKindRegistry"/>.</para>
    /// </summary>
    static IEnumerable<AtomRejection> ValidateAtom(ConsumableDefRow def, ConsumableCoreAtom atom)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null)
        {
            yield return ConsumableRules.Fail(ConsumableRules.BadValue,
                $"'{def.ContainerId}' core atom '{atom.AtomId}' names kind '{atom.KindId}', which is not " +
                "in the registry");
            yield break;
        }

        var when = ParseWhen(atom.WhenJson);

        // §3.3 / failure mode 6: EffectBag.FireGrant short-circuits both PassesOverlayFilters and
        // _proc.TryPass on the lifecycle path, so an atom that fires through it gets NO chance roll and
        // NO internal cooldown whatever it authors. Accepting either key would be a silent no-op, which
        // is exactly what ParamNotHonoured exists to refuse -- enforced, not documented.
        if (when.TryGetValue("chance", out var chance) && chance.ValueKind != JsonValueKind.Null)
            yield return ConsumableRules.Fail(ConsumableRules.ParamNotHonoured,
                $"'{def.ContainerId}' core atom '{atom.AtomId}' authors chance={chance}; the grant " +
                "lifecycle path honours neither chance nor icd_ms (EffectBag.FireGrant short-circuits " +
                "PassesOverlayFilters and _proc.TryPass), so it would fire 100% of the time");

        if (when.TryGetValue("icd_ms", out var icd) && icd.ValueKind != JsonValueKind.Null)
            yield return ConsumableRules.Fail(ConsumableRules.ParamNotHonoured,
                $"'{def.ContainerId}' core atom '{atom.AtomId}' authors icd_ms={icd}; the grant lifecycle " +
                "path honours no internal cooldown. v1 has no door for a clock to guard (§3.3)");

        if (when.TryGetValue("trigger", out var trig) && trig.ValueKind == JsonValueKind.String)
        {
            var name = trig.GetString()!;
            if (!AtomTriggers.IsKnown(name))
                yield return ConsumableRules.Fail(ConsumableRules.TriggerNotAllowed,
                    $"'{def.ContainerId}' core atom '{atom.AtomId}' names trigger '{name}', which is not " +
                    "in the vocabulary. §4.2 asked for an eighth trigger called 'OnUse'; what shipped is " +
                    "OnActivate (A18b), and there must not be a second name for one concept");
            else if (!kind.AllowsTrigger(name))
                yield return ConsumableRules.Fail(ConsumableRules.TriggerNotAllowed,
                    $"'{def.ContainerId}': {atom.KindId} does not carry '{name}'");
        }
        // No trigger at all is fine and is NOT defaulted: a permanent modifier declares none
        // (definitions §14.2), and stat.modify is TriggerOptional precisely so that invariant survived
        // OnActivate being added to its list.
    }

    /// <summary>
    /// §6.3 / failure mode 5, the <b>invisible nerf</b>: every core atom must be legal in EVERY runtime
    /// the <c>use_context</c> names, checked at catalog load rather than discovered in play.
    /// </summary>
    static IEnumerable<AtomRejection> ValidateRuntimes(ConsumableDefRow def, ConsumableCoreAtom atom)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        if (kind is null) yield break;

        foreach (var ctx in def.UseContexts)
            foreach (var runtime in Consumables.UseContexts.RuntimesFor(ctx))
                if (kind.SupportIn(runtime) == RuntimeState.None)
                    yield return ConsumableRules.Fail(ConsumableRules.RuntimeUnsupported,
                        $"'{def.ContainerId}' is usable in '{Consumables.UseContexts.Wire(ctx)}', which " +
                        $"runs on {runtime}, but core atom '{atom.AtomId}' is kind '{atom.KindId}' whose " +
                        $"{runtime} support is None — it would bind and do nothing, which is the " +
                        "invisible nerf this check exists to refuse (failure mode 5)");
    }

    static IReadOnlyDictionary<string, JsonElement> ParseWhen(string? whenJson)
    {
        var empty = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(whenJson)) return empty;
        try
        {
            using var doc = JsonDocument.Parse(whenJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return empty;
            var map = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject()) map[p.Name] = p.Value.Clone();
            return map;
        }
        catch (JsonException)
        {
            return empty;
        }
    }
}
