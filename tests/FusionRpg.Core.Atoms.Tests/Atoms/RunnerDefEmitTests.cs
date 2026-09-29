using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E26 acceptance (spec-runner-def-emit.md). Before this module, every atom the compilability
/// classifier routed to <see cref="AtomRunner"/> threw <c>unknown effect_id</c> the moment it was
/// granted — <c>AtomRunner.cs:206-209</c> said so in its own words: E7 emits defs only for the
/// compiled path, and nothing emitted one per runner entry. <see cref="AtomCompiler.EmitRunnerDefs"/>
/// closes that gap, keying each def's <c>EffectId</c> on <c>entry.AtomId</c> exactly as
/// <c>AtomRunner.Dispatch</c> already names it on the grant (<c>AtomRunner.cs:216</c>).
///
/// <para><b>The overlay-key mismatch, discovered building this module.</b>
/// <c>AtomRunner.RollValues</c> writes the grant overlay under the raw authored param name ("amount",
/// "damage"), but <c>EffectOverlayMerge.AllowedByAction</c> only accepts the op-as-key rewritten form
/// (<c>flat</c>/<c>increased</c>/<c>more</c>/<c>replace</c>/<c>flag</c>) for
/// <c>stat.modify</c>/<c>stat.derived</c>, and does not allow "damage" at all for
/// <c>board.action</c>. No def shape fixes that on this module's own terms — the mismatch lives in
/// <c>AtomRunner</c>/<c>EffectOverlayMerge</c>, and fixing it there is out of E26's contract (§4: "this
/// module adds no opcode and no kind"). So <c>EmitRunnerDefs</c> refuses translation for those three
/// kinds when they carry a non-fixed value, by id, rather than emit a def that is guaranteed to fail
/// <c>TryValidateOverlayForDef</c> at grant time — exactly §4's own rule that a silent omission is
/// unacceptable but a named refusal is the outcome this module owes.</para>
/// </summary>
public class RunnerDefEmitTests
{
    // ---- fixtures, matching AtomCompilerTests' own helpers so both suites build the same shapes ----

    static AtomRow Atom(
        string family,
        string kindId = "stat.modify",
        string paramsJson = "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":45}",
        string whenJson = "{}") => new()
    {
        AtomId = AtomRow.DeriveId(family, "", 1),
        KindId = kindId,
        FamilyId = family,
        Variant = "",
        Tier = 1,
        Name = family,
        ParamsJson = paramsJson,
        WhenJson = whenJson,
    };

    static AtomRow Strike(string whenJson, string paramsJson = "{\"amount\":-120}") =>
        Atom("atom.strike", "resource.delta", paramsJson, whenJson);

    static string Leaf(string leaf, string subject, string value) =>
        "{\"leaf\":\"" + leaf + "\",\"subject\":\"" + subject + "\",\"value\":" + value + "}";

    static string When(string trigger, string? predicate = null)
    {
        var parts = new List<string> { "\"trigger\":\"" + trigger + "\"" };
        if (predicate is not null) parts.Add("\"predicate\":" + predicate);
        return "{" + string.Join(",", parts) + "}";
    }

    static RunnerBinding Bind(RunnerEntry entry, string bindingId = "b1", int priority = 0) =>
        new(bindingId, priority, "player:1", entry);

    /// <summary>Compiles one atom to its (necessarily single) runner entry, then emits its def through
    /// the real production path — <see cref="AtomCompiler.EmitRunnerDefs"/> then
    /// <see cref="AtomPushCodec.ToDef"/>, the same conversion the wire codec uses.</summary>
    static (RunnerEntry Entry, EffectDef Def) CompileOne(AtomRow atom)
    {
        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);
        Assert.Empty(compiled.Rejected);
        var entry = Assert.Single(compiled.Runtime);

        var (defs, rejected) = AtomCompiler.EmitRunnerDefs(new[] { entry });
        Assert.Empty(rejected);
        var def = Assert.Single(defs);

        return (entry, AtomPushCodec.ToDef(def));
    }

    static SimEffectHost HostWith(RunnerEntry entry, EffectDef def, ulong runSeed = 1)
    {
        var host = new SimEffectHost(seed: 3, matchKey: "m1");
        host.WithCatalog(EffectSeedCatalog.CreateAll().Append(def));
        host.UseRunner(new[] { Bind(entry) }, runSeed);
        return host;
    }

    // ---- 1. the headline defect: a per-hit roll range is granted and executes end to end -----------

    [Fact]
    public void An_atom_with_a_per_hit_roll_range_is_granted_and_executes_end_to_end()
    {
        var atom = Strike(When(EffectTriggers.OnDamageDealt),
            "{\"amount\":{\"min\":-120,\"max\":-80,\"roll\":\"onApply\"}}");

        var (entry, def) = CompileOne(atom);
        var host = HostWith(entry, def);

        Assert.False(host.Funnel.HasPending);
        host.HitDealt(attackerSide: "plant");

        // Flushed by the same event that caused it, and no unknown-effect-id throw along the way.
        Assert.False(host.Funnel.HasPending);
        Assert.True(host.Bag.HasGrantForEffect(entry.AtomId));
    }

    // ---- 2. one test per runner route — every reason Compilability sends an atom here ---------------

    [Fact]
    public void The_capPerMatch_route_dispatches_without_throwing()
    {
        var atom = Atom("atom.sun-tap", "resource.economy",
            "{\"currency\":\"sun\",\"op\":\"add\",\"amount\":25,\"capPerMatch\":3}",
            When(EffectTriggers.OnDamageDealt));

        var (entry, def) = CompileOne(atom);
        Assert.Equal(3, entry.Limits.CapPerMatch);

        var host = HostWith(entry, def);
        host.HitDealt(attackerSide: "plant");

        Assert.True(host.Bag.HasGrantForEffect(entry.AtomId));
    }

    [Fact]
    public void The_charges_route_dispatches_without_throwing()
    {
        var atom = Strike("{\"trigger\":\"OnDamageDealt\",\"charges\":3}");

        var (entry, def) = CompileOne(atom);
        Assert.Equal(3, entry.Limits.Charges);

        var host = HostWith(entry, def);
        host.HitDealt(attackerSide: "plant");

        Assert.True(host.Bag.HasGrantForEffect(entry.AtomId));
    }

    [Fact]
    public void The_everyHits_route_dispatches_without_throwing()
    {
        var atom = Strike("{\"trigger\":\"OnDamageDealt\",\"everyHits\":2}");

        var (entry, def) = CompileOne(atom);
        Assert.Equal(2, entry.Limits.EveryHits);

        var host = HostWith(entry, def);
        host.HitDealt(attackerSide: "plant"); // 1st — meter not yet reached
        Assert.False(host.Bag.HasGrantForEffect(entry.AtomId));
        host.HitDealt(attackerSide: "plant"); // 2nd — fires

        Assert.True(host.Bag.HasGrantForEffect(entry.AtomId));
    }

    [Fact]
    public void The_maxStacks_route_dispatches_without_throwing()
    {
        var atom = Strike("{\"trigger\":\"OnDamageDealt\",\"maxStacks\":2}");

        var (entry, def) = CompileOne(atom);

        var host = HostWith(entry, def);
        host.HitDealt(attackerSide: "plant");

        Assert.True(host.Bag.HasGrantForEffect(entry.AtomId));
    }

    [Fact]
    public void The_non_legacy_predicate_route_dispatches_without_throwing()
    {
        // hpBelowMilli has no legacy-filter leaf, so Compilability routes it to the runner (rule 1)
        // rather than folding it into the compiled overlay's `filters`. PredicateCompiler rejects an
        // out-of-[0,1000] hpBelowMilli value, and the mapper defaults a missing HP fact to full
        // (RunnerEventMapper.FullHpMilli = 1000) — no in-range threshold is ever below that default,
        // so the target's HP fact has to be supplied explicitly for the gate to have anything to gate.
        var predicate = "{\"op\":\"and\",\"children\":[" + Leaf("hpBelowMilli", "target", "500") + "]}";
        var atom = Strike(When(EffectTriggers.OnDamageDealt, predicate));

        var (entry, def) = CompileOne(atom);
        Assert.False(entry.IsUnconditional);

        var host = HostWith(entry, def);
        host.HpMilliOf = _ => 300;
        host.HitDealt(attackerSide: "plant");

        Assert.True(host.Bag.HasGrantForEffect(entry.AtomId));
    }

    // ---- 3. the id contract AtomRunner.Dispatch depends on ------------------------------------------

    [Fact]
    public void The_emitted_defs_EffectId_equals_the_entrys_AtomId()
    {
        var atom = Strike(When(EffectTriggers.OnDamageDealt),
            "{\"amount\":{\"min\":1,\"max\":9,\"roll\":\"onApply\"}}");

        var (entry, def) = CompileOne(atom);

        Assert.Equal(entry.AtomId, def.EffectId);
    }

    // ---- 4. the def declares what the overlay will actually carry -----------------------------------

    [Fact]
    public void A_grant_whose_overlay_carries_the_rolled_key_passes_overlay_validation()
    {
        // resource.delta is one of the SAFE kinds: AtomRunner.RollValues writes the overlay under
        // "amount", and EffectActions.ApplyResourceDelta's own allowlist accepts "amount" directly —
        // no op-as-key rewrite involved. This is the positive twin of test 5's refusal.
        var atom = Strike(When(EffectTriggers.OnDamageDealt),
            "{\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onApply\"}}");

        var (_, def) = CompileOne(atom);

        var overlay = new Dictionary<string, object?> { ["amount"] = 15 };
        var ok = EffectOverlayMerge.TryValidateOverlayForDef(def.Actions, overlay, out var error);

        Assert.True(ok, error);
    }

    // ---- 5. planted violation: an untranslatable entry is refused by id, never silently dropped -----

    [Theory]
    [InlineData("stat.modify", "{\"channel\":\"maxHp\",\"op\":\"flat\",\"amount\":{\"min\":10,\"max\":20,\"roll\":\"onApply\"}}")]
    [InlineData("stat.derived", "{\"channel\":\"combat.power.fire\",\"op\":\"flat\",\"amount\":{\"min\":5,\"max\":15,\"roll\":\"onApply\"}}")]
    [InlineData("board.action", "{\"op\":\"cherry\",\"damage\":{\"min\":100,\"max\":200,\"roll\":\"onApply\"}}")]
    public void An_untranslatable_entry_is_refused_by_id_not_silently_dropped(string kindId, string paramsJson)
    {
        var atom = Atom("atom.mismatch", kindId, paramsJson, When(EffectTriggers.OnDamageDealt));

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);
        Assert.Empty(compiled.Rejected); // the CLASSIFIER accepts it into the runner — the refusal is E26's

        var entry = Assert.Single(compiled.Runtime);
        var (defs, rejected) = AtomCompiler.EmitRunnerDefs(new[] { entry });

        Assert.Empty(defs);
        var refusal = Assert.Single(rejected);
        Assert.Equal(entry.AtomId, refusal.AtomId);
        Assert.Equal(AtomRejectionReason.ParamNotHonoured, refusal.Reason);
    }

    [Fact]
    public void A_mismatch_kind_entry_with_no_value_param_at_all_still_translates()
    {
        // The refusal is scoped to entries that actually carry a Values bound. stat.modify/derived
        // always declare `amount`/Required:true, so every runner-routed entry of those two kinds has
        // one — but board.action's `damage` is optional, so a board.action atom with none at all (routed
        // to the runner here by an unrelated non-legacy predicate) has nothing for AtomRunner to write
        // onto the overlay, and there is no mismatch to refuse.
        var predicate = "{\"op\":\"and\",\"children\":[" + Leaf("hpBelowMilli", "target", "2000") + "]}";
        var atom = Atom("atom.mow-no-damage", "board.action",
            "{\"op\":\"cherry\"}",
            When(EffectTriggers.OnDamageDealt, predicate));

        var compiled = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 1);
        var entry = Assert.Single(compiled.Runtime);
        Assert.Empty(entry.Values);

        var (defs, rejected) = AtomCompiler.EmitRunnerDefs(new[] { entry });

        Assert.Empty(rejected);
        Assert.Single(defs);
    }

    // ---- 6. regression: the compiled path is untouched by this module's addition --------------------

    [Fact]
    public void A_compiled_path_atoms_def_id_and_actions_are_unchanged_alongside_a_runner_atom()
    {
        var compiledAtom = Strike(When(EffectTriggers.OnDamageDealt));
        var runnerAtom = Strike(When(EffectTriggers.OnTimer),
            "{\"amount\":{\"min\":1,\"max\":9,\"roll\":\"onApply\"}}") with
        { AtomId = "atom.rolling.t1", FamilyId = "atom.rolling" };

        var before = AtomCompiler.Compile(new[] { compiledAtom }, RuntimeId.Lawn, catalogRevision: 1);
        var after = AtomCompiler.Compile(new[] { compiledAtom, runnerAtom }, RuntimeId.Lawn, catalogRevision: 1);

        var defBefore = Assert.Single(before.Defs);
        var defAfter = Assert.Single(after.Defs); // runnerAtom contributes no COMPILED def
        Assert.Equal(defBefore.EffectId, defAfter.EffectId);
        Assert.Equal(defBefore.Actions.Select(a => a.Action), defAfter.Actions.Select(a => a.Action));

        var grantBefore = Assert.Single(before.Compiled);
        var grantAfter = Assert.Single(after.Compiled);
        Assert.Equal(grantBefore.GrantId, grantAfter.GrantId);
        Assert.Equal(grantBefore.EffectId, grantAfter.EffectId);

        // EmitRunnerDefs only ever adds — it is called over bindings, never over compiled.Defs — so
        // the compiled-path payload the codec builds is unaffected by whether a runner atom rides
        // alongside it in the same push.
        var (runnerDefs, _) = AtomCompiler.EmitRunnerDefs(after.Runtime);
        Assert.DoesNotContain(runnerDefs, d => d.EffectId == defAfter.EffectId);
    }

    // ---- 7. the staleness trap: a compiler-code change causes a re-push -----------------------------

    [Fact]
    public void A_receiver_at_the_current_revision_but_an_older_emitter_version_gets_a_full_push()
    {
        var atom = Strike(When(EffectTriggers.OnDamageDealt));
        var catalog = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 7);

        // Same catalog revision, but the receiver last learned a stale (or, on a cold/pre-E26
        // receiver, unknown = null) emitter version — CatalogRevision alone cannot see a
        // compiler-code change, because it is a stamp over seed DATA (spec §3.3).
        var stale = AtomPushCodec.BuildPayload(
            catalog, Enumerable.Empty<RunnerBinding>(), matchSeed: 1,
            receiverRevision: 7, receiverEmitterVersion: AtomPushCodec.EmitterVersion - 1);

        Assert.False(stale.UpToDate);
        Assert.NotEmpty(stale.Defs);
        Assert.Equal(AtomPushCodec.EmitterVersion, stale.EmitterVersion);

        var current = AtomPushCodec.BuildPayload(
            catalog, Enumerable.Empty<RunnerBinding>(), matchSeed: 1,
            receiverRevision: 7, receiverEmitterVersion: AtomPushCodec.EmitterVersion);

        Assert.True(current.UpToDate);
        Assert.Equal(AtomPushCodec.EmitterVersion, current.EmitterVersion); // stamped even when UpToDate
    }

    [Fact]
    public void A_cold_receiver_with_no_known_emitter_version_gets_a_full_push_even_at_the_right_revision()
    {
        var atom = Strike(When(EffectTriggers.OnDamageDealt));
        var catalog = AtomCompiler.Compile(new[] { atom }, RuntimeId.Lawn, catalogRevision: 3);

        var payload = AtomPushCodec.BuildPayload(
            catalog, Enumerable.Empty<RunnerBinding>(), matchSeed: 1,
            receiverRevision: 3, receiverEmitterVersion: null);

        Assert.False(payload.UpToDate);
    }
}
