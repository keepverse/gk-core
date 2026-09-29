using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Combat.Element;
// The owner-key grammar a durable grant is stamped with, and its matching ownerKind, live with the
// binder that consumes them — one place, so the producer and the rewrite can never drift apart.
using FusionRpg.Core.Match;
using FusionRpg.Core.Power;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// Turns atoms into something the shipped machine already runs.
///
/// <para><b>Compile what Foundation can express; hand the rest to the runner.</b> The output is the
/// same <c>EffectGrantDto</c> shape the Funnel and the bag already accept, so the sealed layer is
/// untouched. This is a compiler and never an applier: it does not apply, order, merge, or mitigate,
/// and it calls neither Unity, the Writer, nor the bag.</para>
///
/// <para>Runs <b>server-side</b>. E19 delivers the output; the injector never holds content rows.</para>
/// </summary>
public static class AtomCompiler
{
    /// <summary>
    /// Compile one catalog revision.
    ///
    /// <para>Atoms are grouped by <c>COALESCE(icd_key, atom_id)</c> first: a group becomes <b>one</b>
    /// def carrying the union of its triggers, which is how a multi-trigger def keeps a single ICD
    /// clock after being split into several atoms (definitions §14.1). The runtime never learns a new
    /// key — <c>EffectDef.Triggers</c> has always been a list.</para>
    /// </summary>
    /// <param name="grantOwnerKeys">
    /// item-ideal.md, equip-runtime (module 5) — the owner keys each atom's COMPILED (passive) grant
    /// must carry, keyed by <c>atom_id</c>. <b>Null (the default) is the shipped behaviour verbatim:</b>
    /// one grant per ICD group at <see cref="EffectOwnerKeys.Match"/>, with the grant id it has always
    /// had. Supplied, it is what lets one live specimen's passive gear stay scoped to that specimen
    /// instead of reaching the whole match — see <see cref="OwnerKeysFor"/> for the one shape it
    /// deliberately declines to scope.
    ///
    /// <para>The compiler never learns what an owner IS: a caller that knows the bindings (today
    /// <c>AtomPushService.Build</c>, via <see cref="UniqueOwnerBinder.OwnerKeyForDurableGrant"/>) hands
    /// it finished key strings. Dedup by ICD key is untouched — the def is still one per group, and
    /// this only decides how many grants point at it.</para>
    /// </param>
    /// <param name="externalRefs">
    /// `patron-absorption` (spec-patron-absorption.md, 2026-09-06): resolves a <c>ValueSpec.ExternalRef</c>
    /// marker to a real number — the exact same shape <paramref name="curves"/> already has. The
    /// compiler never learns what a ref id MEANS; a caller that knows (today
    /// <c>AtomPushService.Build</c>, backed by a live <c>RpgStore.Patron.cs</c> lookup calling the real
    /// <c>PatronPolicy.AuraMilli</c> for whichever player the push is for) hands back the number.
    /// Null (the default) means an atom carrying <c>ExternalRef</c> throws when compiled — never
    /// silently prices at zero.
    /// </param>
    /// <param name="ownerElementPrimary">
    /// Phase 7 F3.1 (combat-unification, 2026-09-07): the SAME "per-owner compile-time context" shape
    /// <paramref name="ownerLevel"/>/<paramref name="ownerTheta"/> already use, applied to element
    /// typing. Null (the default) is the shipped behaviour verbatim — no `elementPayload` is baked
    /// into any compiled grant. Non-null, a compiled <c>ApplyResourceDelta</c> grant with no
    /// already-authored `elementPayload` gets one built by calling <see cref="HybridPayload.Build"/>
    /// directly (never a second implementation of its arithmetic) — this keeps `AtomCompiler` a pure
    /// compiler, same discipline as every other owner-context parameter here: it never applies,
    /// merges, or calls Unity/the Writer/the bag, and the sealed Foundation `EffectBag` needs no
    /// change at all, since the compiled grant already carries whatever a hand-authored one would.
    /// </param>
    /// <param name="ownerElementSecondary">The owner's secondary element, or null for a single-typed
    /// owner. Mirrors <see cref="ownerElementPrimary"/> — only meaningful together with it.</param>
    /// <param name="hybridSecondaryWeightMilli">
    /// The SAME `hybrid.secondaryWeightMilli` weight <see cref="HybridPayload.Build"/> already takes
    /// on web-battle (`battle.v5.json`, Phase 7 F1) — passed explicitly rather than read from
    /// `BattleRuleset` directly, matching how <paramref name="powerTuning"/> is a parameter and not a
    /// static reach-through: this compiler stays free of hidden global-state dependencies. The real
    /// caller (`AtomPushService.Build`) sources it from the same `BattleRuleset.HybridSecondaryWeightMilli`
    /// the server already configures. Defaults to 0 — the pre-Phase-7 shipped value — so an omitted
    /// argument reproduces today's exact behaviour.
    /// </param>
    public static CompiledCatalog Compile(
        IEnumerable<AtomRow> atoms,
        RuntimeId runtime,
        long catalogRevision,
        Func<string, CurveTable?>? curves = null,
        Func<string, int>? statusBit = null,
        Func<string, int>? elementId = null,
        bool hostIsPlanner = false,
        int ownerLevel = 1,
        int? ownerTheta = null,
        PowerTuning? powerTuning = null,
        Func<string, IReadOnlyCollection<string>?>? grantOwnerKeys = null,
        Func<string, long>? externalRefs = null,
        ElementTypeId? ownerElementPrimary = null,
        ElementTypeId? ownerElementSecondary = null,
        int hybridSecondaryWeightMilli = 0)
    {
        var defs = new List<EffectDefDto>();
        var compiled = new List<EffectGrantDto>();
        var compiledIds = new List<string>();
        var runner = new List<RunnerEntry>();
        var rejected = new List<CompileRejection>();

        // Ordered so the same revision bakes to identical bytes. Without this the grouping would
        // follow enumeration order and a push could not be compared to what the injector holds.
        var ordered = atoms.OrderBy(a => a.AtomId, StringComparer.Ordinal).ToList();

        foreach (var group in ordered
                     .GroupBy(a => a.EffectiveIcdKey(), StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var members = group.OrderBy(a => a.AtomId, StringComparer.Ordinal).ToList();
            var verdicts = members
                .Select(a => (Atom: a, Verdict: Compilability.Classify(a, runtime, hostIsPlanner)))
                .ToList();

            foreach (var (atom, verdict) in verdicts.Where(v => v.Verdict.Path == AtomPath.Rejected))
                rejected.Add(new CompileRejection(atom.AtomId, verdict.Rejection, verdict.Reason));

            var live = verdicts.Where(v => v.Verdict.Path != AtomPath.Rejected).ToList();
            if (live.Count == 0) continue;

            // A group compiles only if every member does. One member needing the runner sends the
            // whole group there, because splitting it would split the ICD clock they were grouped to
            // share — and that is a behaviour change, not an optimisation.
            var allCompilable = live.All(v => v.Verdict.Path == AtomPath.Compiled);

            if (allCompilable && live.Count > 0)
            {
                var compilable = live.Select(v => v.Atom).ToList();
                var (def, grants) = EmitDefAndGrant(
                    group.Key, compilable, curves, ownerLevel, ownerTheta, powerTuning, grantOwnerKeys, externalRefs,
                    ownerElementPrimary, ownerElementSecondary, hybridSecondaryWeightMilli);
                defs.Add(def);
                compiled.AddRange(grants);
                compiledIds.AddRange(compilable.Select(m => m.AtomId));
            }
            else
                foreach (var (atom, _) in live)
                    runner.Add(EmitRunnerEntry(atom, group.Key, curves, statusBit, elementId, ownerLevel));
        }

        return new CompiledCatalog(catalogRevision, defs, compiled, compiledIds, runner, rejected);
    }

    /// <summary>
    /// One def for a whole ICD group, carrying the <b>union</b> of its members' triggers, and one
    /// grant per owner that sourced it.
    ///
    /// <para>A triggerless <c>stat.modify</c> / <c>stat.derived</c> must be emitted as
    /// <c>EffectType.Passive</c>. <c>EffectDef.EffectType</c> defaults to <c>Triggered</c>, and the
    /// bag fires the lifecycle pair only when the def is Passive <b>or</b> its trigger list contains
    /// <c>OnGranted</c> — so a triggerless atom compiled with the default would never apply at all
    /// (definitions §14.2).</para>
    /// </summary>
    static (EffectDefDto Def, IReadOnlyList<EffectGrantDto> Grants) EmitDefAndGrant(
        string icdKey, IReadOnlyList<AtomRow> members, Func<string, CurveTable?>? curves, int ownerLevel,
        int? ownerTheta, PowerTuning? powerTuning,
        Func<string, IReadOnlyCollection<string>?>? grantOwnerKeys = null,
        Func<string, long>? externalRefs = null,
        ElementTypeId? ownerElementPrimary = null,
        ElementTypeId? ownerElementSecondary = null,
        int hybridSecondaryWeightMilli = 0)
    {
        // The UNION of the group's triggers, on ONE def. This is what keeps a multi-trigger def's
        // single ICD clock after it was split into several atoms: EffectDef.Triggers has always been
        // a list, and the bag's ICD key deliberately excludes the trigger (definitions 14.1).
        var triggers = members
            .Select(m => TriggerOf(m.WhenJson))
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        // A permanent modifier declares no trigger. EffectType defaults to Triggered, and the bag
        // fires the lifecycle pair only when the def is Passive OR its triggers contain OnGranted --
        // so emitting the default here would mean the modifier never applies at all (14.2).
        var effectType = triggers.Count == 0 ? EffectTypes.Passive : EffectTypes.Triggered;

        // One action per DISTINCT action, not per member. Atoms that differ only in their trigger
        // are one thing the effect does, fired several ways — `fx.shield_grant` is three atoms and
        // one GrantShield. Emitting per member gave it three, so it granted three shields where it
        // grants one, inside the module whose entire acceptance is byte-identical plans.
        var actions = new List<EffectDefActionDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var seq = 1;
        foreach (var member in members)
        {
            if (OpcodeOf(member.KindId) is not { } action) continue;

            var pars = ResolvedParams(member, curves, ownerLevel, ownerTheta, powerTuning, externalRefs);
            if (!seen.Add(action + "|" + Fingerprint(pars))) continue;

            actions.Add(new EffectDefActionDto { Seq = seq++, Action = action, Params = pars });
        }

        var def = new EffectDefDto
        {
            // The ICD key IS the def's identity, verbatim. Two reasons it is not decorated:
            //
            // It was decorated, and the decoration was wrong. `icd_key` defaults to `atom_id`, and
            // `atom_id` already opens with `atom.` (the family-id grammar, validated at E4) — so
            // prefixing produced `atom.atom.vitality.t1`. Nothing asserted the id, so it shipped.
            //
            // And a migrated effect must keep the id its existing grants already name. A player's
            // stored grant says `fx.butter_on_hit`; if the compiled def answered to anything else,
            // the grant would resolve to nothing the moment the def became a row. Authoring
            // `icd_key: fx.butter_on_hit` is how a migration says "this is the same effect".
            EffectId = icdKey,
            EffectType = effectType,
            Name = members[0].Name,
            Enabled = true,
            SourceTag = "atom",
            Triggers = triggers,
            Actions = actions,
        };

        // Chance, ICD and legacy filters ride the overlay, exactly as a hand-authored grant does.
        var overlay = new Dictionary<string, object?>(StringComparer.Ordinal);
        var when = Read(members[0].WhenJson);

        if (when.TryGetValue("chance", out var chance) && chance.TryGetInt32(out var c) && c < 1000)
            overlay["chance"] = c / 1000.0;
        if (when.TryGetValue("icd_ms", out var icd) && icd.TryGetInt32(out var ms) && ms > 0)
            overlay["icd_ms"] = ms;

        if (when.TryGetValue("predicate", out var pred) && pred.ValueKind == JsonValueKind.Object)
        {
            var filters = LegacyFilters(pred);
            if (filters.Count > 0) overlay["filters"] = filters;
        }

        // Phase 7 F3.1 (combat-unification, 2026-09-07): the owner's own dual-typing rides the
        // overlay too, exactly like chance/icd_ms/filters above — but only for a group that actually
        // deals damage (ApplyResourceDelta), only when the owner has a primary element to build from,
        // and only when nothing already authored one (no real atom kind does today, but a future one
        // might, and authored content must always win over this default). Built by calling
        // HybridPayload.Build directly — never a second implementation of its arithmetic — so this
        // compiled grant carries exactly what a hand-authored one with the same elements would.
        if (ownerElementPrimary is { } primary
            && !overlay.ContainsKey("elementPayload")
            && actions.Any(a => string.Equals(a.Action, EffectActions.ApplyResourceDelta, StringComparison.OrdinalIgnoreCase)))
        {
            // lawn-combat-wire T10: the overlay-shape mapping moved to HybridPayload.BuildOverlay so
            // the lawn's per-actor basic-attack grant (which bakes an owner's element directly, never
            // through this compiler) produces byte-identical elementPayload shape from ONE
            // implementation. Behaviour here is unchanged — same Build call, same "length>0" gate.
            var built = HybridPayload.BuildOverlay(primary, ownerElementSecondary, hybridSecondaryWeightMilli);
            if (built != null)
                overlay["elementPayload"] = built;
        }

        // One grant per owner that sourced this group. With no owner map that is exactly one grant at
        // `match`, with the id and every other field this method has always emitted.
        var grants = new List<EffectGrantDto>();
        foreach (var ownerKey in OwnerKeysFor(members, grantOwnerKeys))
            grants.Add(new EffectGrantDto
            {
                GrantId = GrantIdFor(icdKey, ownerKey),
                EffectId = def.EffectId,
                OwnerKind = UniqueOwnerBinder.OwnerKindForDurableGrant(ownerKey),
                OwnerKey = ownerKey,
                PluginId = "atom",
                Priority = 0,
                // Copied per grant rather than shared: two grants aliasing one dictionary would let a
                // later mutation of one silently rewrite the other's chance / icd_ms / filters.
                Overlay = overlay.Count == 0
                    ? null
                    : new Dictionary<string, object?>(overlay, StringComparer.Ordinal),
            });

        return (def, grants);
    }

    /// <summary>
    /// The owner keys a compiled group's grants carry.
    ///
    /// <para><b>The group is the unit, not the atom</b>, because the def is per ICD group and a grant
    /// points at a def. So the keys come from the group's members — and only when every member agrees
    /// on the same owner set.</para>
    ///
    /// <para><b>The one shape this declines to scope, and why.</b> A group whose members were sourced
    /// by DIFFERENT owners (owner A wears atom X, owner B wears atom Y, and the two share an authored
    /// <c>icd_key</c>) compiles to ONE def carrying the union of X's and Y's actions — that is what an
    /// ICD group is. Handing that def to both owners would give each the other's action; today's
    /// single match-wide grant at least does it once rather than twice. Neither is right, and making
    /// it right needs a def per owner, which would collide on <c>EffectDefDto.EffectId</c> — the ICD
    /// key IS the def's identity. So a heterogeneous group falls back to the shipped behaviour
    /// verbatim rather than amplifying it, and the real fix is a content rule (do not share an
    /// <c>icd_key</c> across containers held by different owners), not a compiler change.</para>
    /// </summary>
    static IReadOnlyList<string> OwnerKeysFor(
        IReadOnlyList<AtomRow> members, Func<string, IReadOnlyCollection<string>?>? grantOwnerKeys)
    {
        var matchOnly = new[] { EffectOwnerKeys.Match };
        if (grantOwnerKeys is null || members.Count == 0) return matchOnly;

        List<string>? agreed = null;
        foreach (var member in members)
        {
            var supplied = grantOwnerKeys(member.AtomId);

            // Ordinal sort, so "the same owners" is one comparison and the bake stays byte-identical
            // for a revision no matter what order the caller collected the bindings in.
            var keys = supplied is null || supplied.Count == 0
                ? matchOnly.ToList()
                : supplied.Distinct(StringComparer.Ordinal)
                          .OrderBy(k => k, StringComparer.Ordinal)
                          .ToList();

            if (agreed is null) { agreed = keys; continue; }
            if (!agreed.SequenceEqual(keys, StringComparer.Ordinal)) return matchOnly;
        }

        return agreed is null ? matchOnly : agreed;
    }

    /// <summary>
    /// <c>atom:{icdKey}</c> for the match-scoped grant — unchanged, because a held grant is looked up
    /// by this id — and <c>atom:{icdKey}@{ownerKey}</c> for an owner-scoped one, which is what keeps
    /// two owners' grants on the same def from colliding in the bag's id-keyed store.
    /// </summary>
    static string GrantIdFor(string icdKey, string ownerKey) =>
        string.Equals(ownerKey, EffectOwnerKeys.Match, StringComparison.Ordinal)
            ? "atom:" + icdKey
            : "atom:" + icdKey + "@" + ownerKey;

    /// <summary>
    /// E26: one <see cref="EffectDefDto"/> per translatable <see cref="RunnerEntry"/>, so
    /// <c>AtomRunner.Dispatch</c>'s grant resolves against a real def instead of throwing "unknown
    /// effect_id" (<see cref="AtomRunner"/>, whose own comment names this exact gap at the call site
    /// this method closes). Deduped by <see cref="RunnerEntry.AtomId"/> — several bindings can
    /// reference the same atom; a def is per-atom, not per-binding, exactly like the compiled path's
    /// per-ICD-group def.
    ///
    /// <para><b>Untranslatable, not silently dropped.</b> <c>stat.modify</c> / <c>stat.derived</c>
    /// rewrite an authored magnitude into an op-as-key overlay slot (<c>flat</c>/<c>increased</c>/…
    /// — see <see cref="ToOpcodeShape"/>), and <c>board.action</c>'s <c>damage</c> is not in
    /// <c>EffectOverlayMerge</c>'s allowed set at all. <c>AtomRunner.RollValues</c> only ever writes
    /// the AUTHORED param name onto the overlay ("amount" / "damage"), so a runner-routed atom of one
    /// of these three kinds would either throw at <c>EffectOverlayMerge.TryValidateOverlayForDef</c>
    /// ("amount" is not an allowed key for `stat.modify`/`stat.derived`) or silently apply a
    /// hardcoded default (`board.action`'s dropped `damage`, E28's own finding) — the exact "accepted,
    /// then nothing forever" shape this whole layer exists to refuse. Refusing here, by id, converts
    /// that into an authoring-time refusal instead. This is <see cref="AtomRejectionReason.ParamNotHonoured"/>
    /// exactly — "the key is declared but the executor drops it for this configuration." <b>The fix
    /// for the mismatch itself belongs to `AtomRunner`/`EffectOverlayMerge`, not to this module</b> —
    /// spec-runner-def-emit.md's own boundary is not to resolve a rolled value or touch the runner.</para>
    /// </summary>
    public static (IReadOnlyList<EffectDefDto> Defs, IReadOnlyList<CompileRejection> Rejected) EmitRunnerDefs(
        IEnumerable<RunnerEntry> entries)
    {
        if (entries is null) throw new ArgumentNullException(nameof(entries));

        var defs = new List<EffectDefDto>();
        var rejected = new List<CompileRejection>();

        var distinct = entries
            .GroupBy(e => e.AtomId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.AtomId, StringComparer.Ordinal);

        foreach (var entry in distinct)
        {
            if (string.IsNullOrEmpty(entry.Trigger))
            {
                rejected.Add(new CompileRejection(entry.AtomId, AtomRejectionReason.UnknownTrigger,
                    "a runner entry with no trigger can never be dispatched by AtomRunner.OnEvent " +
                    "(TriggerIndex indexes bindings by trigger ordinal)"));
                continue;
            }

            var opcode = OpcodeOf(entry.KindId);
            if (opcode is null)
            {
                rejected.Add(new CompileRejection(entry.AtomId, AtomRejectionReason.UnknownKind,
                    $"'{entry.KindId}' has no FA opcode"));
                continue;
            }

            // The three kinds whose overlay key does not match the raw param name AtomRunner writes.
            // Only a problem when the entry actually carries a per-hit value — a stat.modify/derived
            // or board.action atom that reached the runner purely for a stateful key or a predicate,
            // with no Value-kind param at all, has nothing that would hit the mismatch.
            var isKeyMismatchKind = entry.KindId is "stat.modify" or "stat.derived" or "board.action";
            if (isKeyMismatchKind && entry.Values.Count > 0)
            {
                rejected.Add(new CompileRejection(entry.AtomId, AtomRejectionReason.ParamNotHonoured,
                    $"'{entry.KindId}' rolls its magnitude under the authored param name, which " +
                    "EffectOverlayMerge does not accept for this action on the runner path " +
                    "(stat.modify/stat.derived need the op-as-key form flat/increased/more/replace/" +
                    "flag; board.action's damage is not allowlisted at all) — the fix belongs to " +
                    "AtomRunner/EffectOverlayMerge, not runner-def-emit"));
                continue;
            }

            defs.Add(new EffectDefDto
            {
                EffectId = entry.AtomId,
                EffectType = EffectTypes.Triggered,
                Name = entry.AtomId,
                Enabled = true,
                SourceTag = "atom-runner",
                Triggers = new List<string> { entry.Trigger! },
                Actions = new List<EffectDefActionDto>
                {
                    new()
                    {
                        Seq = 1,
                        Action = opcode,
                        // Static params only (channel, element, currency, status — whatever the kind's
                        // non-Value params carry). The rolled magnitude is deliberately absent: it
                        // arrives on the grant overlay per proc, resolved by AtomRunner.RollValues at
                        // dispatch time, never baked here (spec-runner-def-emit.md §4 — "resolving a
                        // rolled value at emit time would be a second roll and would break replay").
                        Params = new Dictionary<string, object?>(entry.Params, StringComparer.Ordinal),
                    },
                },
            });
        }

        return (defs, rejected);
    }

    /// <summary>Kind to the FA opcode its sink implements. Null for kinds with no opcode. Public so a
    /// guard test (E41: Ui-attached kinds never compile to a state-writing opcode) can read it without
    /// duplicating this switch.</summary>
    public static string? OpcodeOf(string kindId) => kindId switch
    {
        "stat.modify" => EffectActions.ModifyStat,
        "stat.derived" => EffectActions.ModifyDerivedStat,
        "resource.delta" => EffectActions.ApplyResourceDelta,
        "resource.economy" => EffectActions.Economy,
        "status.apply" => EffectActions.ApplyStatus,
        "status.clear" => EffectActions.ClearStatus,
        "shield.grant" => EffectActions.GrantShield,
        "spawn.entity" => EffectActions.SpawnEntity,
        "board.action" => EffectActions.BoardAction,
        "grid.spawn" => EffectActions.SpawnGridItem,
        "grid.clear" => EffectActions.ClearGridItem,
        "box.set" => EffectActions.SetBoxType,
        // E35 (spec-match-modify.md §2.5).
        "match.modify" => EffectActions.ModifyMatch,
        // E36 (spec-wave-control.md §2.1).
        "wave.control" => EffectActions.WaveControl,
        // E37 (spec-projectile-control.md §2b).
        "bullet.modify" => EffectActions.BulletModify,
        // E41 (spec-ui-attach-point.md §2b).
        "ui.present" => EffectActions.PresentUi,
        // base-defense `siege-construction` (decision 27, 2026-09-06).
        "structure.place" => EffectActions.PlaceStructure,
        _ => null,
    };

    static RunnerEntry EmitRunnerEntry(
        AtomRow atom, string icdKey, Func<string, CurveTable?>? curves,
        Func<string, int>? statusBit, Func<string, int>? elementId, int ownerLevel)
    {
        var when = Read(atom.WhenJson);

        ICompiledPredicate predicate = PredicateCompiler.Always;
        if (when.TryGetValue("predicate", out var predEl) && predEl.ValueKind == JsonValueKind.Object
            && AtomJson.TryReadPredicate(predEl, out var tree).IsOk && tree is not null)
        {
            PredicateCompiler.TryCompile(tree, statusBit, out predicate, elementId);
        }

        var chance = when.TryGetValue("chance", out var cEl) && cEl.TryGetInt32(out var c) ? c : 1000;
        var icdMs = when.TryGetValue("icd_ms", out var iEl) && iEl.TryGetInt32(out var i) ? i : 0;

        var pars = Read(atom.ParamsJson);

        return new RunnerEntry(
            atom.AtomId,
            atom.KindId,
            TriggerOf(atom.WhenJson),
            predicate,
            chance,
            icdMs,
            icdKey,
            BoundsOf(atom, curves, ownerLevel),
            LimitsOf(when, pars),
            NonValueParams(atom, pars));
    }

    /// <summary>
    /// The per-binding state keys, read from <c>when_json</c> and <c>params_json</c> alike —
    /// <see cref="Compilability"/> routes on either, so reading only one would drop half of them.
    /// A key declared in both takes its params value, that being the schema-declared home.
    /// </summary>
    static RunnerLimits LimitsOf(
        Dictionary<string, JsonElement> when, Dictionary<string, JsonElement> pars) =>
        new(Int(when, pars, "capPerMatch"),
            Int(when, pars, "charges"),
            Int(when, pars, "everyHits"),
            Int(when, pars, "maxStacks", "max_stacks"));

    static int Int(
        Dictionary<string, JsonElement> when, Dictionary<string, JsonElement> pars, params string[] names)
    {
        foreach (var name in names)
            if (pars.TryGetValue(name, out var p) && p.TryGetInt32(out var v)) return v;
        foreach (var name in names)
            if (when.TryGetValue(name, out var w) && w.TryGetInt32(out var v)) return v;
        return -1;
    }

    /// <summary>
    /// Everything the schema does not call a <see cref="ParamKind.Value"/>. Those are already in
    /// <c>Values</c> with their curve applied; these are the strings and flags a dispatch is built
    /// from, and an entry without them knows a magnitude but not what it is a magnitude of.
    /// </summary>
    static Dictionary<string, object?> NonValueParams(AtomRow atom, Dictionary<string, JsonElement> pars)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var (key, raw) in pars)
        {
            var def = kind?.Params.Defs.FirstOrDefault(d =>
                string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase));
            if (def is { Kind: ParamKind.Value }) continue;
            result[key] = Plain(raw);
        }

        return result;
    }

    /// <summary>
    /// Value bounds with their curve <b>already applied</b>. No curve row travels to the injector, so
    /// a value it cannot scale must arrive pre-scaled (D9). `input: level` on an OnApply spec is
    /// refused at E4 load precisely so this stays possible.
    /// </summary>
    static Dictionary<string, ValueBounds> BoundsOf(
        AtomRow atom, Func<string, CurveTable?>? curves, int ownerLevel)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        var pars = Read(atom.ParamsJson);
        var bounds = new Dictionary<string, ValueBounds>(StringComparer.Ordinal);
        if (kind is null) return bounds;

        foreach (var def in kind.Params.Defs)
        {
            if (def.Kind != ParamKind.Value) continue;
            if (!pars.TryGetValue(def.Name, out var raw)) continue;
            if (!AtomJson.TryReadValueSpec(raw, out var spec).IsOk) continue;

            bounds[def.Name] = ValueBounds.Of(spec, MultiplierFor(spec, curves, ownerLevel));
        }

        return bounds;
    }

    static int MultiplierFor(ValueSpec spec, Func<string, CurveTable?>? curves, int ownerLevel)
    {
        if (string.IsNullOrWhiteSpace(spec.CurveId) || curves is null) return 1000;

        var curve = curves(spec.CurveId!);
        return curve is null ? 1000 : curve.MultiplierAt(ownerLevel);
    }

    static Dictionary<string, object?> ResolvedParams(
        AtomRow atom, Func<string, CurveTable?>? curves, int ownerLevel, int? ownerTheta = null,
        PowerTuning? powerTuning = null, Func<string, long>? externalRefs = null)
    {
        var kind = AtomKindRegistry.Get(atom.KindId);
        var pars = Read(atom.ParamsJson);
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (kind is null) return result;

        foreach (var (key, raw) in pars)
        {
            var def = kind.Params.Defs.FirstOrDefault(d =>
                string.Equals(d.Name, key, StringComparison.OrdinalIgnoreCase));

            if (def is null || def.Kind != ParamKind.Value)
            {
                result[key] = Plain(raw);
                continue;
            }

            if (!AtomJson.TryReadValueSpec(raw, out var spec).IsOk) continue;

            // `P0.2`: an event-linked spec has no number to bake — `spec.Min` is unused (always 0) —
            // so a marker rides through the compiled params instead, resolved live by
            // `DamagePacketBuilder.FromOverlay` once the firing event exists (spec-value-spec-and-
            // curve.md, "Event-linked magnitudes"). `AtomRowValidator` already refused this shape on
            // any kind but `resource.delta` at load, so nothing downstream needs to re-check the kind.
            if (spec.EventField is not null)
            {
                result[key] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["eventField"] = spec.EventField,
                    ["multiplierMilli"] = spec.MultiplierMilli,
                };
                continue;
            }

            // T6.2: unlike eventField, an owner's own Θ is known at COMPILE time (not something a
            // hit produces), so this resolves to a plain number right here — no marker, no deferral,
            // and `ToOpcodeShape` below needs no change. `PatronPolicy.AuraMilli`'s own exact
            // arithmetic: kMilli · PowerLadder.Value(Θ) / 1000, widened before multiplying and
            // divided once (docs/architecture/numeric-types.md's overflow discipline — Θ can be large, per-mille intermediates
            // are 1000× closer to the ceiling). Rejects rather than guesses when the caller compiled
            // with no owner Θ/tuning in scope — a powerLadder atom is unauthorable content until a
            // real caller supplies both, never silently priced at zero.
            if (spec.PowerLadder)
            {
                if (ownerTheta is not { } theta || powerTuning is null)
                    throw new InvalidOperationException(
                        $"{atom.AtomId}: a powerLadder value spec was compiled with no ownerTheta/powerTuning " +
                        "supplied — AtomCompiler.Compile needs both to resolve it, never a silent default.");

                var pThetaValue = new PowerLadder(powerTuning).Value(theta);

                // B3 (spec-tree-binder.md §3.5, §7): tree-binder's kMicro is per-million, not
                // per-mille — at per-mille, gated-deep stores kMilli=0 for 12 of 40 nodes (silently
                // inert). Non-zero KMicro selects this WIDENED path; the existing KMilli path below
                // is completely untouched, so every authored (JSON-sourced) powerLadder atom keeps
                // its existing int-refusal ceiling and behavior exactly as shipped.
                if (spec.PowerLadderKMicro != 0)
                {
                    result[key] = checked(spec.PowerLadderKMicro * pThetaValue / 1_000_000);
                    continue;
                }

                result[key] = checked((int)((long)spec.PowerLadderKMilli * pThetaValue / 1000));
                continue;
            }

            // T6.2's second gap: `clamp(base + level, 0, cap)` — `ownerLevel` is the SAME
            // pre-existing, non-nullable parameter every curve-scaled value already reads (defaults
            // to 1, exactly like every other caller that never sets it), so this needs no "missing
            // context" rejection the way `powerLadder`'s Θ does — there is no unset state to guard.
            if (spec.ClampedLevelScale)
            {
                var unclamped = checked((long)spec.ClampedLevelScaleBaseMilli + ownerLevel);
                result[key] = (int)Math.Clamp(unclamped, 0, spec.ClampedLevelScaleCapMilli);
                continue;
            }

            // patron-absorption (spec-patron-absorption.md, 2026-09-06): "referenced, not
            // re-expressed" — the compiler never learns what the ref MEANS, it only ever invokes the
            // callback a caller handed it (the exact same shape `curves` already has). Throws rather
            // than guesses when no callback was supplied, or the callback does not recognise this
            // specific id — an externalRef atom is unauthorable content until a real caller supplies
            // both, never silently priced at zero, matching powerLadder's own established rule.
            if (spec.ExternalRef is not null)
            {
                if (externalRefs is null)
                    throw new InvalidOperationException(
                        $"{atom.AtomId}: an externalRef value spec ('{spec.ExternalRef}') was compiled " +
                        "with no externalRefs callback supplied — AtomCompiler.Compile needs one to " +
                        "resolve it, never a silent default.");

                result[key] = checked((int)externalRefs(spec.ExternalRef));
                continue;
            }

            // Compiled atoms never carry a range — rule 3 sent those to the runner — so one number.
            result[key] = CurveTable.ApplyMilli(spec.Min, MultiplierFor(spec, curves, ownerLevel));
        }

        return ToOpcodeShape(atom.KindId, result);
    }

    /// <summary>
    /// The atom vocabulary spelled the way the opcode reads it.
    ///
    /// <para><b>FA1 names the operation with the key, not with a value.</b> It reads <c>flat</c>,
    /// <c>increased</c> and <c>more</c> (InjectorEffectActionSink.cs:93–101) and knows nothing about
    /// <c>op</c> or <c>amount</c>. A compiled <c>stat.modify</c> carrying <c>{channel, op, amount}</c>
    /// matched none of them and fell through to the <c>mods.Count == 0</c> arm — which applies a
    /// <b>flat zero</b>. Every atom-authored stat modifier was a real modifier of no size, and no
    /// test noticed because no real content had been compiled yet.</para>
    ///
    /// <para>The op vocabulary is validated at load, so an unrecognised one cannot arrive here.</para>
    /// </summary>
    static Dictionary<string, object?> ToOpcodeShape(string kindId, Dictionary<string, object?> pars)
    {
        if (kindId is not ("stat.modify" or "stat.derived")) return pars;
        if (!pars.TryGetValue("op", out var opRaw)) return pars;
        if (!pars.TryGetValue("amount", out var amount)) return pars;

        var op = opRaw?.ToString()?.ToLowerInvariant();
        if (string.IsNullOrEmpty(op)) return pars;

        pars.Remove("op");
        pars.Remove("amount");
        pars[op!] = amount;
        return pars;
    }

    /// <summary>Order-independent identity for a param set — two actions are the same action or not.</summary>
    static string Fingerprint(Dictionary<string, object?> pars) =>
        string.Join(",", pars.OrderBy(p => p.Key, StringComparer.Ordinal)
                             .Select(p => p.Key + "=" + p.Value));

    /// <summary>The `filters` block a grant overlay understands: side, typeId, actorIsKiller.</summary>
    static Dictionary<string, object?> LegacyFilters(JsonElement predicate)
    {
        var filters = new Dictionary<string, object?>(StringComparer.Ordinal);
        Walk(predicate);
        return filters;

        void Walk(JsonElement node)
        {
            if (node.ValueKind != JsonValueKind.Object) return;

            if (node.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            {
                foreach (var kid in kids.EnumerateArray()) Walk(kid);
                return;
            }

            if (!node.TryGetProperty("leaf", out var leafEl)) return;
            if (!node.TryGetProperty("value", out var valEl)) return;

            switch (leafEl.GetString()?.ToLowerInvariant())
            {
                case "sideis": filters["side"] = valEl.GetString(); break;
                case "typeidis": filters["typeId"] = valEl.GetInt32(); break;
                case "actoriskiller": filters["actorIsKiller"] = valEl.ValueKind == JsonValueKind.True
                    || (valEl.ValueKind == JsonValueKind.Number && valEl.GetInt32() != 0); break;
            }
        }
    }

    static string? TriggerOf(string? whenJson)
    {
        var when = Read(whenJson);
        return when.TryGetValue("trigger", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
    }

    // E28 fix #7 (spec-param-parity.md §3 row 7, box.set.cells[]): Array/Object used to fall through
    // to `el.ToString()` — the raw JSON text as an opaque string, structurally useless to a reader
    // expecting a list of cells. `cells` is ParamKind.Array's only declared use today; nothing else
    // in the shipped corpus goes through this arm, so widening it here changes no existing content.
    // E28 fix #7 (spec-param-parity.md §3 row 7, box.set.cells[]): Array/Object used to fall through
    // to `el.ToString()` — the raw JSON text as an opaque string, structurally useless to a reader
    // expecting a list of cells. `cells` is ParamKind.Array's only declared use today; nothing else
    // in the shipped corpus goes through this arm, so widening it here changes no existing content.
    // E28 fix #7 (spec-param-parity.md §3 row 7, box.set.cells[]): Array/Object used to fall through
    // to `el.ToString()` — the raw JSON text as an opaque string, structurally useless to a reader
    // expecting a list of cells. `cells` is ParamKind.Array's only declared use today; nothing else
    // in the shipped corpus goes through this arm, so widening it here changes no existing content.
    //
    // Found while adding it: the Number arm's ternary — `TryGetInt32(out var i) ? i : el.GetDouble()`
    // — has ALWAYS produced a boxed `double`, never `int`, regardless of which branch runs. The `?:`
    // operator picks ONE static type for both branches before boxing to `object?`, and since `int`
    // widens to `double` but not the reverse, the compiler silently converts `i` to `double` in every
    // case. `(object)i` on the true branch breaks that unification — both arms are then `object`, and
    // boxing preserves whichever CLR type actually applies. Pre-existing since this method was
    // written; harmless downstream today because `JsonOverlay.GetInt`'s `Convert.ToInt32` tolerates a
    // boxed `double`, but a real type defect worth closing rather than leaving for the next reader who
    // trusts the ternary's apparent intent.
    static object? Plain(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? (object)i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => el.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.Object => el.EnumerateObject()
            .ToDictionary(p => p.Name, p => Plain(p.Value), StringComparer.Ordinal),
        _ => el.ToString(),
    };

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
