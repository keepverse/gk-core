using FusionRpg.Contracts;
using FusionRpg.Core.Aura;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Creatures;
using FusionRpg.Core.Effects.Atoms;
using FusionRpg.Core.Match;
using FusionRpg.Core.Power;
using FusionRpg.Core.Saves;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Aptitudes;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Data;

namespace FusionRpg.Server;

/// <summary>
/// Builds the compiled-output push for one owner (spec-compiled-push.md, E19).
///
/// <para><b>Cold, by construction.</b> This runs at Hello and at bind time — never between an event
/// and its apply. The injector rolls its own dice locally so the hot loop never waits; what travels
/// from here is the compiled content and the <b>seed</b>, which is what makes those local rolls
/// replayable (definitions §13 D5).</para>
///
/// <para>What leaves this class is already resolved: compiled grants, defs, and runner entries whose
/// predicates are flat int ops. No atom row, container row or curve row is ever put on the wire — if
/// one had to be, the compile/run split would have leaked.</para>
/// </summary>
public sealed class AtomPushService
{
    readonly RpgStore _store;

    /// <summary>
    /// `mods-absorption` (T6.1, item-ideal.md equip-runtime module 5) — the owner list one save's
    /// live atom push actually needs: the human empire's own scope plus every currently-<see
    /// cref="UniqueActorPhases.ActiveBound"/> specimen (deployed AND bound, not merely rostered) of
    /// EVERY empire of the save. Extracted 2026-09-06 from <c>RpgHub.BuildApplyCommand</c>'s own inline
    /// loop (Hello) so a SECOND real call site — a mid-session re-push triggered by a unique actor's
    /// own phase transition, not a fresh connection — builds the identical union rather than a
    /// hand-rolled copy that could silently drift from the Hello path's own rule.
    ///
    /// <para><b>save-identity SE4.26 ("the match's runtime"):</b> was <c>OwnersForPlayer</c>, reading
    /// <c>ListUniqueActors(playerId)</c> — the SAME read SE4.26 scoped to one empire's own roster for
    /// the "which specimens does this empire own" question. This is a DIFFERENT question ("who is on
    /// the board right now"), so it must not narrow the same way: Zomboss's own ActiveBound specimens
    /// still need their trait atoms pushed (the push runs per owning row), and scoping this read to the
    /// human empire alone would silently strip them. So it walks every empire of the save explicitly.
    /// </para>
    /// </summary>
    public static List<OwnerScope> OwnersForSave(RpgStore store, long saveId)
    {
        var owners = new List<OwnerScope>
        {
            new(OwnerKind.Player, saveId.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };
        foreach (var empire in store.EmpiresOf(saveId))
            foreach (var specimen in store.ListUniqueActors(new EmpireRef(new SaveId(saveId), empire.Empire)).Items)
                if (string.Equals(specimen.Phase, UniqueActorPhases.ActiveBound, StringComparison.Ordinal))
                    owners.Add(new OwnerScope(OwnerKind.UniqueActor, specimen.InstanceId));
        return owners;
    }

    /// <summary>
    /// The ONE place an <c>effects.grants.apply</c> wire payload is assembled (T6.2, 2026-09-06).
    ///
    /// <para><b>Why this exists at all: the compiled grants were being dropped.</b>
    /// <see cref="AtomPushCodec.BuildPayload"/> fills <see cref="AtomPushDto.Grants"/> from
    /// <c>catalog.Compiled</c> — every passive, non-triggered atom (<c>stat.derived</c> /
    /// <c>stat.modify</c> with no trigger) compiles to a grant there rather than to a runner entry.
    /// Both real call sites then hand-rolled their own payload dictionary carrying <c>defs</c> and
    /// <c>runnerBindings</c> and <b>never that list</b>, so the entire compiled half of the push was
    /// inert on the wire — a def with no grant naming it is content the bag holds and never applies.
    /// That predates the equip-runtime work entirely: it was true of the original Player-only push.</para>
    ///
    /// <para><b>One key, not two.</b> The compiled grants go into the SAME <c>grants</c> array as the
    /// session snapshot rather than a key of their own, because that array is the only thing on the
    /// receiving side that applies a grant at all: the injector's <c>RunEffectsGrantsApply</c> loops
    /// <c>grants[]</c> → <c>RunEffectGrant</c> → <c>EffectRuntime.Grant</c>, and
    /// <c>AtomPushReceiver.Install</c> deliberately does NOT apply <see cref="AtomPushDto.Grants"/>
    /// ("the command runner's existing grant loop owns that" — its own doc comment) because that loop
    /// does injector-only work the receiver must not duplicate: resolving <c>entity:selected</c>,
    /// normalising the owner key, and refusing an <c>instance:</c> owner on the Hot path. A separately
    /// named key would have been read by nothing. <see cref="AtomPushDto.Grants"/> is itself declared
    /// <c>[JsonPropertyName("grants")]</c>, so this is the shape the contract always described.</para>
    ///
    /// <para><b>Session grants first, compiled appended.</b> Purely additive — the pre-existing half
    /// keeps its exact order and content, and on the receiving side a later entry would only win a
    /// <c>GrantId</c> collision, which <c>atom:{icdKey}</c> ids cannot realistically have with a
    /// session grant.</para>
    ///
    /// <para><b><c>grants</c> is always present and always an array</b>, even when empty and even when
    /// <paramref name="atoms"/> is null: the injector refuses the WHOLE command — the atom half
    /// included, <c>InstallAtomPush</c> never reached — when the key is absent or not an array
    /// ("effects.grants.apply: missing grants[]").</para>
    /// </summary>
    /// <param name="atoms">The compiled push, or null when its build failed (Foundation grants still ship).</param>
    /// <param name="sessionGrants">The Hot Effect session snapshot; null for an atoms-only re-push.</param>
    public static Dictionary<string, object?> BuildApplyPayload(
        AtomPushDto? atoms,
        IReadOnlyList<EffectGrantDto>? sessionGrants = null)
    {
        var grants = new List<EffectGrantDto>();
        if (sessionGrants is not null) grants.AddRange(sessionGrants);
        if (atoms is not null) grants.AddRange(atoms.Grants);

        var payload = new Dictionary<string, object?> { ["grants"] = grants };
        if (atoms is null) return payload;

        payload["defs"] = atoms.Defs;
        payload["runnerBindings"] = atoms.RunnerBindings;
        payload["catalogRevision"] = atoms.CatalogRevision;
        payload["contentHash"] = atoms.ContentHash;
        payload["matchSeed"] = atoms.MatchSeed;
        payload["matchKey"] = atoms.MatchKey;
        payload["upToDate"] = atoms.UpToDate;
        // E26 stamp. AtomPushDto's own doc says "always present, on every payload"; neither call site
        // ever sent it. Harmless to a receiver that ignores it, and required by the contract.
        payload["emitterVersion"] = atoms.EmitterVersion;
        return payload;
    }

    public AtomPushService(RpgStore store) => _store = store ?? throw new ArgumentNullException(nameof(store));

    /// <summary>
    /// `patron-absorption` (spec-patron-absorption.md, 2026-09-06) — the live callback
    /// <see cref="ValueSpec.ExternalRef"/> atoms resolve through. Backed by
    /// <see cref="PatronEndpoints.Compute"/>, the SAME real logic that endpoint itself reports (patron
    /// row → profile/actor → the player's own Θ → <c>PatronPolicy.Aura</c>) — never a second
    /// derivation. Computed fresh on every call, never persisted, so a promotion or a switch is
    /// reflected on the very next push with nothing to refresh or go stale.
    ///
    /// <para>`patron.aura`'s own 12 atoms (one per real element × power/defense,
    /// <c>gk-data/packs/fusion/data/seed/atoms/patron-aura.json</c>) are all authored statically — every player gets the
    /// same 12 atoms pushed, and only the ONE or TWO matching the pushed player's own patron element(s)
    /// resolve non-zero here; every other element's ref resolves to a harmless `0` (a flat `+0`
    /// contributes nothing, the additive identity — not a value this method invents, the same shape
    /// `AuraMilli`'s own zero-for-absent-secondary already has).</para>
    ///
    /// <para><b>backlog-clear AU2:</b> `aura.magnitude.{AuraId}.{channel}` (`gk-data/packs/fusion/data/seed/atoms/aura-content.json`)
    /// resolves independently of Patron — checked by prefix BEFORE the Patron gate below, because that
    /// gate returns 0 for every unrecognised id when the player has no patron set, which would have
    /// silently zeroed every aura too. See <see cref="AuraMagnitude.ReferenceChannelValue"/> for what
    /// the value is (a structural reference, not yet live per-commander scaling — named there).</para>
    /// </summary>
    Func<string, long> BuildExternalRefs(IReadOnlyList<OwnerScope> owners)
    {
        return refId =>
        {
            if (TryAuraMagnitudeRef(refId, out var auraId, out var channel))
                return AuraChannelReferenceValue(auraId, channel);

            var playerOwner = owners.FirstOrDefault(o => o.Kind == OwnerKind.Player);
            if (playerOwner == default || !long.TryParse(playerOwner.Key, out var playerId))
                return 0;

            var computed = PatronEndpoints.Compute(_store, playerId);
            if (computed is not { } c)
                return 0; // no patron set — every patron-aura ref resolves to 0, never an error

            var (_, aura) = c;
            return refId switch
            {
                _ when TryElementSuffix(refId, "patron.auraPowerMilli.", out var el) =>
                    ForElement(el, aura.ElementPrimary, aura.PowerMilli, aura.ElementSecondary, aura.SecondaryPowerMilli),
                _ when TryElementSuffix(refId, "patron.auraDefenseMilli.", out var el) =>
                    ForElement(el, aura.ElementPrimary, aura.DefenseMilli, aura.ElementSecondary, aura.SecondaryDefenseMilli),
                _ => throw new InvalidOperationException($"unknown externalRef id: {refId}"),
            };
        };

        static bool TryElementSuffix(string refId, string prefix, out string element)
        {
            element = refId.StartsWith(prefix, StringComparison.Ordinal) ? refId[prefix.Length..] : "";
            return element.Length > 0;
        }

        static long ForElement(string element, string primary, long primaryMilli, string? secondary, long secondaryMilli) =>
            string.Equals(element, primary, StringComparison.Ordinal) ? primaryMilli
            : secondary is not null && string.Equals(element, secondary, StringComparison.Ordinal) ? secondaryMilli
            : 0;
    }

    const string AuraMagnitudePrefix = "aura.magnitude.";

    static bool TryAuraMagnitudeRef(string refId, out string auraId, out string channel)
    {
        auraId = ""; channel = "";
        if (!refId.StartsWith(AuraMagnitudePrefix, StringComparison.Ordinal)) return false;

        var rest = refId[AuraMagnitudePrefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot >= rest.Length - 1) return false;

        auraId = rest[..dot];
        channel = rest[(dot + 1)..];
        return true;
    }

    /// <summary>AU2's reference split: which of this aura's own declared channels
    /// <paramref name="channel"/> is, then <see cref="AuraMagnitude.ReferenceChannelValue"/> for its
    /// even share of the aura's total reference budget.
    ///
    /// <para><b>W7 (battle-derived-wire T4): <c>internal</c>, not <c>private</c>.</b> The web-match
    /// setup producer needs the SAME aura→channel value the atom push already resolves, so it calls
    /// this rather than growing a second aura resolver (the task's own "never a new aura resolver"
    /// rule). The body is unchanged and duplicated nowhere.</para></summary>
    internal static long AuraChannelReferenceValue(string auraId, string channel)
    {
        if (!AuraContentCatalog.IsKnown(auraId)) return 0; // an id no catalog row names -- inert, not an error

        var channels = AuraContentCatalog.Resolve(auraId).GrantChannels;
        var index = -1;
        for (var i = 0; i < channels.Count; i++)
        {
            if (!string.Equals(channels[i], channel, StringComparison.Ordinal)) continue;
            index = i;
            break;
        }
        if (index < 0) return 0; // channel not declared for this aura -- inert, not an error

        return AuraMagnitude.ReferenceChannelValue(
            index, channels.Count, PowerTuningHub.Tuning.Curve.PinValue,
            AuraTuningHub.Tuning, AptitudeTuningHub.Tuning);
    }

    /// <summary>
    /// Phase 7 F3.2 (combat-unification, 2026-09-07): the owner-element context
    /// <see cref="FusionRpg.Core.Effects.Atoms.AtomCompiler.Compile"/> bakes into a compiled
    /// `ApplyResourceDelta` grant's `elementPayload`, mirroring <c>ownerLevel</c>'s own "resolve once
    /// per <c>Build</c> call" shape exactly.
    ///
    /// <para><b>Named scope limit, not silently guessed:</b> this compile is over the UNION of
    /// several owners at once (<c>Build</c>'s own doc comment — a player's grants plus every deployed
    /// specimen), and <c>AtomCompiler.Compile</c>'s owner-context parameters (like <c>ownerLevel</c>
    /// before this) are ONE value per whole compile, not one per owner. Applying a single specimen's
    /// element to a batch that might carry several DIFFERENTLY-typed specimens would silently mistype
    /// the others, which is worse than the pre-Phase-7 shape of carrying none. So this resolves a real
    /// element pair only when the batch names EXACTLY ONE <see cref="OwnerKind.UniqueActor"/> — the
    /// common real case (one specimen's own push at bind/deploy time) — and returns null otherwise,
    /// leaving every multi-specimen batch exactly as inert as it was before this task, named here
    /// rather than silently accepted as solved.</para>
    /// </summary>
    (ElementTypeId Primary, ElementTypeId? Secondary)? OwnerElements(IReadOnlyList<OwnerScope> owners)
    {
        var specimenOwners = owners.Where(o => o.Kind == OwnerKind.UniqueActor).ToList();
        if (specimenOwners.Count != 1) return null;

        var actor = _store.GetUniqueActor(specimenOwners[0].Key);
        if (actor is null) return null;

        var index = new LawnElementIndex(CreatureSpeciesCatalog.All);
        if (!index.TryGet(actor.Side, actor.TypeId, out var species)) return null;

        return (species.ElementPrimary, species.ElementSecondary);
    }

    /// <summary>
    /// The full set for one owner, or an empty up-to-date reply when the receiver already holds this
    /// catalog revision.
    /// </summary>
    /// <param name="receiverRevision">What the injector says it holds; null on cold start.</param>
    /// <param name="receiverEmitterVersion">
    /// E26: what <see cref="AtomPushCodec.EmitterVersion"/> the injector last learned (from its Hello).
    /// Null on cold start or against a pre-E26 injector that has never reported the field — distinct
    /// from a real version, so neither is mistaken for the other in the short-circuit below.
    /// </param>
    public AtomPushDto Build(
        OwnerScope owner,
        BindContext ctx,
        ulong matchSeed,
        string? matchKey = null,
        long? receiverRevision = null,
        int? ownerLevel = null,
        int? receiverEmitterVersion = null) =>
        Build(new[] { owner }, ctx, matchSeed, matchKey, receiverRevision, ownerLevel, receiverEmitterVersion);

    /// <summary>
    /// The same build, over several owner scopes at once — the shape module 5 (`equip-runtime`)
    /// named as the missing half of the live lawn push: a player's own grants plus every
    /// <see cref="OwnerKind.UniqueActor"/> specimen currently deployed with them. One compile over
    /// the UNION of every scope's atoms, not one push per owner — two owners sharing an atom (a
    /// player-side buff and an equipped item both touching the same channel) must compile it once,
    /// identically, or the runner would hold two "identical" entries that only accidentally agree.
    /// <see cref="RunnerBinding"/> already carries its own <c>OwnerKey</c> per binding, which is what
    /// makes merging safe: the wire shape was never owner-singular, only this call site was.
    /// </summary>
    public AtomPushDto Build(
        IReadOnlyList<OwnerScope> owners,
        BindContext ctx,
        ulong matchSeed,
        string? matchKey = null,
        long? receiverRevision = null,
        int? ownerLevel = null,
        int? receiverEmitterVersion = null)
    {
        if (owners is null || owners.Count == 0) throw new ArgumentException("at least one owner scope is required", nameof(owners));

        var revision = _store.GetCatalogRevision();

        // The hash is carried even when nothing else is, so a mismatch stays visible in telemetry on
        // a reconnect that delivers no content.
        var contentHash = _store.ComputeContentHash().ToCompact();

        // Two-term short-circuit (E26), mirroring AtomPushCodec.BuildPayload's own: CatalogRevision is
        // a stamp over seed DATA, so a receiver at the right revision but the wrong (or unknown)
        // emitter version still needs the full rebuild — the compiler-code path below is what makes
        // that decision, this early return must not shortcut around it on revision alone.
        if (receiverRevision == revision && receiverEmitterVersion == AtomPushCodec.EmitterVersion)
            return new AtomPushDto
            {
                CatalogRevision = revision,
                ContentHash = contentHash,
                MatchSeed = matchSeed,
                MatchKey = matchKey,
                UpToDate = true,
                EmitterVersion = AtomPushCodec.EmitterVersion,
            };

        // One compile over the distinct atoms behind every accepted binding, across every owner. A
        // per-owner compile would redo the whole classify/bake pass per owner, and two owners sharing
        // an atom would disagree about nothing at real cost — so the union is built first, compiled
        // once, and each owner's bindings are wired against that one shared catalog below.
        var distinct = new Dictionary<string, AtomRow>(StringComparer.Ordinal);
        var acceptedBindings = new List<(BindingRow Binding, IReadOnlyList<AtomRow> Rows)>();

        // item-ideal.md, equip-runtime (module 5): which owners each atom's COMPILED (passive) grant
        // belongs to. The RUNNER path already carried per-owner identity on RunnerBinding.OwnerKey;
        // the compiled path carried none at all, so a live specimen's passive stat.derived/stat.modify
        // gear reached MATCH scope — every plant and zombie on the lawn, not the one wearing it.
        // Sorted so a given revision bakes identical bytes regardless of binding enumeration order.
        var ownersByAtom = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var owner in owners)
        {
            var resolution = _store.ResolveBindings(owner, ctx, ownerLevel);
            foreach (var binding in resolution.Bindings)
            {
                if (resolution.AtomsByBinding is null ||
                    !resolution.AtomsByBinding.TryGetValue(binding.BindingId, out var rows))
                    continue;

                acceptedBindings.Add((binding, rows));

                // Off the BINDING's own scope, never the requested owner's: a resolution can surface a
                // binding at a scope the caller did not name (match-wide rows), and stamping the
                // requested owner onto one of those would scope a shared effect to whoever asked first.
                var ownerKey = UniqueOwnerBinder.OwnerKeyForDurableGrant(binding.Scope);

                foreach (var row in rows)
                {
                    distinct[row.AtomId] = row;
                    if (!ownersByAtom.TryGetValue(row.AtomId, out var keys))
                        ownersByAtom[row.AtomId] = keys = new SortedSet<string>(StringComparer.Ordinal);
                    keys.Add(ownerKey);
                }
            }
        }

        var ownerElements = OwnerElements(owners);
        var catalog = AtomCompiler.Compile(
            distinct.Values.OrderBy(a => a.AtomId, StringComparer.Ordinal).ToList(),
            ctx.Runtime,
            revision,
            curves: id => _store.GetCurve(id),
            ownerLevel: ownerLevel ?? 1,
            grantOwnerKeys: id => ownersByAtom.TryGetValue(id, out var keys) ? keys : null,
            externalRefs: BuildExternalRefs(owners),
            ownerElementPrimary: ownerElements?.Primary,
            ownerElementSecondary: ownerElements?.Secondary,
            // BattleRuleset may genuinely not be configured in a caller that never touches web-battle
            // tuning (several Server.Tests fixtures, confirmed live) — 0 is the safe, correct fallback
            // either way: it is the exact pre-Phase-7 shipped default, not a guess.
            hybridSecondaryWeightMilli: BattleRuleset.IsConfigured ? BattleRuleset.HybridSecondaryWeightMilli : 0);

        var byAtomId = catalog.Runtime.ToDictionary(e => e.AtomId, StringComparer.Ordinal);
        var bindings = new List<RunnerBinding>();

        foreach (var (binding, rows) in acceptedBindings)
        {
            foreach (var row in rows)
            {
                if (!byAtomId.TryGetValue(row.AtomId, out var entry)) continue;

                // The id is (binding, atom), not the binding alone. A container carrying three
                // runner atoms needs three independent ICD clocks and three independent caps — and
                // a shared id would also tie the evaluation sort, making order depend on how the
                // rows happened to arrive. Binding ids are unique per effect_binding row regardless
                // of owner, so merging owners here cannot collide two different owners' entries.
                bindings.Add(new RunnerBinding(
                    binding.BindingId + "#" + row.AtomId,
                    binding.Priority,
                    binding.OwnerKey,
                    entry));
            }
        }

        var payload = AtomPushCodec.BuildPayload(
            catalog, bindings, matchSeed, matchKey, contentHash, receiverRevision, receiverEmitterVersion);

        // P1.5-L (2026-09-07): the real, previously-undiscovered gap this whole compiled-push
        // mechanism has carried since it was built (T6.1, 2026-09-06). `UniqueOwnerBinder.
        // OwnerKeyForDurableGrant` (line ~246 above) stamps every UniqueActor-scoped grant with a
        // durable `instance:{id}` owner key — the ONE key the injector's `RunEffectGrant` refuses
        // outright ("instance: forbidden in Hot; bind to entity:{ptr}"), confirmed live: a real
        // stat.modify equip atom compiled correctly, reached the injector, and was silently dropped
        // at exactly this gate. The rewrite this needs (`UniqueOwnerBinder.BindGrant`, `instance:{id}`
        // -> `entity:{ptr}`) already existed in Core — but its only caller was `UniqueLoadoutSpec`'s
        // OLDER deploy-time `loadoutJson` path, never this one. Every UniqueActor-scoped grant this
        // service has ever sent -- at Hello and at every bind/unbind re-push -- was refused the same
        // way; nothing about this being equip-specific.
        if (payload.Grants.Count > 0)
        {
            var rewritten = new List<EffectGrantDto>(payload.Grants.Count);
            foreach (var grant in payload.Grants)
            {
                if (!StatApplyScope.IsInstanceOwnerKey(grant.OwnerKey)) { rewritten.Add(grant); continue; }

                var instanceId = UniqueOwnerBinder.ExtractInstanceId(grant.OwnerKey);
                var ptr = instanceId is null ? null : _store.GetUniqueActor(instanceId)?.LastPtr;
                // No live ptr yet (rostered/deploying, not yet bound) -- drop rather than send a durable
                // key the hot path is guaranteed to refuse; the next re-push after bind carries it.
                if (string.IsNullOrWhiteSpace(ptr)) continue;
                rewritten.Add(UniqueOwnerBinder.BindGrant(grant, ptr));
            }
            payload.Grants = rewritten;
        }

        // patron-absorption (T6.2b, 2026-09-06): `fx.patron_aura` is never behind a real BindingRow —
        // nothing equips or picks a patron aura the way gear/traits are bound (RpgStore.SetPatron
        // writes rpg_patron, never a BindingRow), so the ResolveBindings loop above never discovers
        // patron.aura's atoms for ANY owner, and its def would never reach this payload without this
        // block. Compiled in ISOLATION (its own AtomCompiler.Compile call) and merged as Defs-ONLY,
        // deliberately discarding its own auto-generated grant: AtomCompiler.Compile always emits at
        // least a match-scoped grant per compiled group (Compile's own "grantOwnerKeys" doc: "Null is
        // the shipped behaviour verbatim: one grant per ICD group at Match") — PatronSecondaryPlugin's
        // existing grant (GrantId "patron:aura", issued only when the player has a patron designated)
        // must stay the SOLE grant for this effect, or the actor would carry two grants naming the
        // same EffectId and GrantedDerivedAtomReader has no de-dup across grants, so the aura's
        // magnitude would apply twice.
        var patronAuraAtoms = PatronAuraAtoms();
        if (patronAuraAtoms.Count > 0)
        {
            var patronCatalog = AtomCompiler.Compile(
                patronAuraAtoms, ctx.Runtime, revision, externalRefs: BuildExternalRefs(owners));
            payload.Defs.AddRange(patronCatalog.Defs);
        }

        return payload;
    }

    /// <summary>`patron.aura`'s own real atoms (`gk-data/packs/fusion/data/seed/atoms/patron-aura.json`), or empty when the
    /// container/atoms are not seeded (a minimal test fixture, e.g.) — never throws for their absence,
    /// since Patron content is not a hard requirement for every caller of this service.</summary>
    IReadOnlyList<AtomRow> PatronAuraAtoms()
    {
        var container = _store.GetContainer("patron.aura");
        if (container is null || container.Atoms.Count == 0) return Array.Empty<AtomRow>();

        var rows = new List<AtomRow>(container.Atoms.Count);
        foreach (var entry in container.Atoms)
        {
            var atom = _store.GetAtom(entry.AtomId);
            if (atom is not null) rows.Add(atom);
        }
        return rows;
    }
}
