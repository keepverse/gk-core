using FusionRpg.Contracts;
using FusionRpg.Core.Actions;
using FusionRpg.Core.Battle;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Data;

/// <summary>
/// A24 (spec-container-effect-resolver-production.md §2): the real, `RpgStore`-backed
/// <see cref="IContainerEffectResolver"/> implementation — the seam A18a's own interface named but
/// left for a production supplier. Compiles every container a real, authored action references, once
/// per call, and hands back both the lookup <see cref="BattleRunState.BindContainers"/> consumes and
/// the matching def list an `onEffectHostReady` callback must push into the same battle's
/// `Host.Bag.Catalog` BEFORE that binder runs (the one working precedent for this pairing,
/// `DistrictAssaultResolver.cs:134-149`, does the same two-piece hand-off for siege's four hardcoded
/// containers; this generalizes it to every real, `RpgStore`-authored one).
/// </summary>
public static class ActionContainerEffectResolverFactory
{
    /// <summary>
    /// Compiled per container, never batched across containers — <see cref="AtomCompiler.Compile"/>
    /// groups by ICD key globally over whatever it is handed (`AtomCompiler.cs:76-77`), so batching
    /// would risk merging two unrelated containers' groups if they ever coincidentally shared an icd
    /// key. Per-container compilation, matching `AtomPushService.PatronAuraAtoms()`'s own granularity,
    /// makes that structurally impossible.
    ///
    /// <para>A container whose every member atom lands on <see cref="AtomPath.Runner"/> (both real
    /// seed atom families, `atom.fortitude`/`atom.vitality`, do — see the spec's own §Objective) yields
    /// zero <c>Defs</c> and is simply absent from the returned resolver's map. This is deliberate: the
    /// EXISTING `BattleRunState.BindContainers` throw for "resolved to nothing" already covers this
    /// case precisely — inventing a second rejection path here would just duplicate it.</para>
    ///
    /// <para>Discards <see cref="CompiledCatalog.Compiled"/> (the compiler's own auto-emitted grants)
    /// on purpose — not a new design choice, the third application of an existing pattern
    /// (<c>AtomPushService.PatronAuraAtoms()</c>, <c>ConstructionActions</c>'s own
    /// <c>onEffectHostReady</c>). <see cref="FusionRpg.Core.Battle.BattleRunState.BindContainers"/> is
    /// the one explicit grant source for a held action's container; registering the compiler's own
    /// grants too would double-grant the same effect to the same actor.</para>
    /// </summary>
    public static (IContainerEffectResolver Resolver, IReadOnlyList<EffectDefDto> Defs, IReadOnlyList<RunnerBinding> RunnerBindings, IReadOnlySet<string> ContainersWithRunnerCoverage) Build(RpgStore store)
    {
        var byContainer = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var defs = new List<EffectDefDto>();
        var runnerBindings = new List<RunnerBinding>();
        // A25: which containers BindContainers must NOT refuse for "resolved to nothing," even with
        // zero Compiled-path effect ids -- built from the SAME per-container loop below (never
        // string-parsed back out of a RunnerBinding's own id), so it can never drift from
        // `runnerBindings` itself.
        var containersWithRunnerCoverage = new HashSet<string>(StringComparer.Ordinal);
        var revision = store.GetCatalogRevision();

        foreach (var container in store.ListActionContainers())
        {
            var atoms = new List<AtomRow>(container.Atoms.Count);
            foreach (var entry in container.Atoms)
            {
                var atom = store.GetAtom(entry.AtomId);
                if (atom is not null) atoms.Add(atom);
            }
            if (atoms.Count == 0) continue;

            var compiled = AtomCompiler.Compile(atoms, RuntimeId.Battle, revision);

            if (compiled.Defs.Count > 0)
            {
                byContainer[container.ContainerId] = compiled.Defs.Select(d => d.EffectId).ToList();
                defs.AddRange(compiled.Defs);
            }

            // A25 (battle-runner-path-integration): a container's Runner-path atoms are NOT part of
            // IContainerEffectResolver's own contract (A18a scoped that interface to Defs only) -- they
            // reach a real battle through the separate `runnerBindings` seam instead. `OwnerKey` is
            // deliberately left empty: `AtomRunner.Dispatch`'s own documented fallback
            // (`string.IsNullOrWhiteSpace(binding.OwnerKey) ? ev.ActorKey : binding.OwnerKey`) then
            // targets whichever actor's own action fired the trigger -- the correct semantic for a
            // container bound to that actor's own held action (a self-effect with variance, matching
            // what atom.fortitude/atom.vitality's own authored shape clearly intends). A runner atom
            // that should instead target the DEFENDER is a real, separate design question this module
            // does not need to answer to prove the mechanism works, and is not silently mis-routed
            // either way -- it would apply to the actor, a named, visible simplification, not a crash
            // or a silent no-op.
            //
            // A triggerless entry (spec's own §Objective finding: atom.fortitude/atom.vitality author
            // no `when.trigger` at all) is skipped here, not passed through -- `TriggerIndex.Build`
            // throws loudly for one ("the compiler and the classifier disagree"), and that throw would
            // fire INSIDE `BattleRunState`'s own constructor, earlier and more confusingly than the
            // already-expected, already-tested `BindContainers` "resolved to nothing" rejection for the
            // exact same container. Skipping here means that existing, well-understood failure stays
            // the one a caller actually sees -- this module adds a real capability, not a new, more
            // confusing way for already-known-bad content to fail.
            foreach (var runnerEntry in compiled.Runtime)
            {
                if (TriggerIndex.Ordinal(runnerEntry.Trigger) < 0) continue;
                runnerBindings.Add(new RunnerBinding(
                    BindingId: container.ContainerId + ":" + runnerEntry.AtomId,
                    Priority: 0,
                    OwnerKey: "",
                    Entry: runnerEntry));
                containersWithRunnerCoverage.Add(container.ContainerId);
            }
        }

        return (new DictionaryContainerEffectResolver(byContainer), defs, runnerBindings, containersWithRunnerCoverage);
    }

    /// <summary>Pushes a resolver build's own <see cref="Build"/> output into a live battle's effect
    /// catalog — the exact `onEffectHostReady` shape `DistrictAssaultResolver.cs:138-149` already
    /// established, generalized so every real caller does not hand-roll the same cast-and-upsert loop.
    /// A no-op when the catalog is not the in-memory implementation (never true in production today,
    /// matching the same defensive `is` check the siege precedent already uses).</summary>
    public static void RegisterInto(BattleEffectHost host, IReadOnlyList<EffectDefDto> defs)
    {
        if (host.Bag.Catalog is not InMemoryEffectCatalog catalog) return;
        foreach (var def in defs) catalog.Upsert(AtomPushCodec.ToDef(def));
    }

    /// <summary>
    /// spec-equip-runtime.md's "Amendment 2026-09-07 — the Battle half, real content": a specimen's
    /// equipped `stat.modify` atoms (virtually all real affix/relic content — confirmed 2026-09-07 that
    /// `EquipAtomSource`'s `stat.derived`-only composer-fold path never sees them) reach a real battle
    /// through this SAME compile/grant pattern, additive to <see cref="Build"/> rather than folded into
    /// it — a container id and a specimen id are different key spaces, and `Build`'s own
    /// <c>byContainer</c> map has no meaningful entry for "this specimen's gear."
    ///
    /// <para><b>Filtered to `stat.modify` ONLY, deliberately.</b> `stat.derived` equip atoms already
    /// have a real, working, separate delivery path (<see cref="Battle.EquipAtomSource"/> →
    /// <see cref="Battle.BattleStatComposer"/>'s compose-time fold). Compiling `stat.derived` atoms
    /// here too would double-apply them — once via the composer fold, once via this grant path — the
    /// exact "two writers, one value" defect `spec-equip-runtime.md`'s own boundary forbids. The two
    /// producers read disjoint atom kinds off the same binding set so neither can ever double-count
    /// the other's contribution.</para>
    /// </summary>
    public static (IReadOnlyList<EffectDefDto> Defs, Func<string, IReadOnlyList<string>> EquipEffectIdsFor)
        BuildEquip(RpgStore store, IReadOnlyList<string> specimenIds)
    {
        var defs = new List<EffectDefDto>();
        var effectIdsBySpecimen = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var revision = store.GetCatalogRevision();

        foreach (var specimenId in specimenIds.Distinct(StringComparer.Ordinal))
        {
            var resolution = store.ResolveBindings(
                new OwnerScope(OwnerKind.UniqueActor, specimenId), new BindContext(RuntimeId.Battle));
            if (resolution.AtomsByBinding is null) continue;

            var statModifyAtoms = resolution.AtomsByBinding.Values
                .SelectMany(atoms => atoms)
                .Where(atom => string.Equals(atom.KindId, "stat.modify", StringComparison.Ordinal))
                .ToList();
            if (statModifyAtoms.Count == 0) continue;

            var compiled = AtomCompiler.Compile(statModifyAtoms, RuntimeId.Battle, revision);
            if (compiled.Defs.Count == 0) continue;

            defs.AddRange(compiled.Defs);
            effectIdsBySpecimen[specimenId] = compiled.Defs.Select(d => d.EffectId).ToList();
        }

        return (defs, specimenId => effectIdsBySpecimen.TryGetValue(specimenId, out var ids)
            ? ids
            : Array.Empty<string>());
    }
}
