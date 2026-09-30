using FusionRpg.Core.Creatures;
using FusionRpg.Core.Stats.Derived;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Creatures;

/// <summary>
/// E27 acceptance (spec-lawn-element-bind.md). The lawn never passed <c>elementTypes:</c> into its
/// `StatContextFactory` calls, so every lawn actor resolved <c>ActorElementTypes.Neutral</c> and 196 of
/// the 267 registered derived channels were inert there — a wiring gap, not an architectural one
/// (`StatContextFactory.cs:33,61`, `InjectorCombatBridge.cs:69-83`). <see cref="LawnElementIndex"/> and
/// <see cref="LawnElementResolver"/> are the Core-testable half of the fix: the `(side, typeId) ->
/// species -> element` lookup and its per-actor-per-match cache. The Injector-side wiring (passing the
/// resolved value into <c>ForPlant</c>/<c>ForZombie</c>) needs a local build and an owner-run live check
/// — this suite proves the piece that CI can actually exercise.
/// </summary>
public class LawnElementResolverTests
{
    static CreatureSpeciesDef Species(
        string id, string side, int gameTypeId,
        ElementTypeId primary = ElementTypeId.Fire, ElementTypeId? secondary = null) => new()
    {
        SpeciesId = id,
        Name = id,
        Side = side,
        GameTypeId = gameTypeId,
        CreatureTypeId = CreatureSpeciesCatalog.CreatureTypeIdFloor + gameTypeId,
        ElementPrimary = primary,
        ElementSecondary = secondary,
        BaseRarity = CreatureRarity.Chaff,
        DeployMode = CreatureDeployMode.PlantAvatar,
        Acquisition = CreatureAcquisition.Summonable,
    };

    // ---- the index -----------------------------------------------------------------------------------

    [Fact]
    public void The_side_and_typeId_pair_is_the_key_typeId_alone_is_not_unique()
    {
        var index = new LawnElementIndex(new[]
        {
            Species("polevaulterzombie", "zombie", 3),
            Species("wallnut", "plant", 3, ElementTypeId.Earth),
        });

        Assert.True(index.TryGet("zombie", 3, out var z));
        Assert.Equal("polevaulterzombie", z.SpeciesId);
        Assert.True(index.TryGet("plant", 3, out var p));
        Assert.Equal("wallnut", p.SpeciesId);
    }

    [Fact]
    public void A_miss_returns_false_not_a_default_species()
    {
        var index = new LawnElementIndex(new[] { Species("a", "plant", 1) });

        Assert.False(index.TryGet("plant", 99, out _));
    }

    [Fact]
    public void A_duplicate_pair_keeps_the_lowest_speciesId_and_reports_the_collision()
    {
        var index = new LawnElementIndex(new[]
        {
            Species("zzz-later", "plant", 5),
            Species("aaa-earlier", "plant", 5),
        });

        Assert.True(index.TryGet("plant", 5, out var kept));
        Assert.Equal("aaa-earlier", kept.SpeciesId);

        var line = Assert.Single(index.Collisions);
        Assert.Contains("aaa-earlier", line);
        Assert.Contains("zzz-later", line);
    }

    // ---- the resolver: element mapping ----------------------------------------------------------------

    [Fact]
    public void A_known_species_resolves_its_primary_element()
    {
        var index = new LawnElementIndex(new[] { Species("s1", "plant", 10, ElementTypeId.Fire) });
        var resolver = new LawnElementResolver(index);

        var (side, elements) = resolver.Resolve("m1", "0xA", () => ("plant", 10, true));

        Assert.Equal("plant", side);
        Assert.Equal(ElementTypeId.Fire, elements.Primary);
        Assert.Null(elements.Secondary);
        Assert.False(elements.IsNeutral);
    }

    [Fact]
    public void A_species_at_gameTypeId_zero_resolves_normally_typeId_zero_is_not_a_sentinel()
    {
        // Real live incident (2026-09-14): gk-data/packs/fusion/data/generated/creatures/Peashooter.json and
        // NormalZombie.json are both "gameTypeId": 0 -- real, ordinary, commonly-spawned creatures, not
        // this codebase's "no entity" sentinel. A resolver that treated typeId == 0 as "board miss"
        // (the original, wrong shape of the 2026-09-14 fix) would degrade every Peashooter and every
        // NormalZombie to Neutral forever, board-registered or not. Found is what must gate this, never
        // the numeric typeId -- this is the regression test for that exact confusion.
        var index = new LawnElementIndex(new[] { Species("Peashooter", "plant", 0, ElementTypeId.Earth) });
        var resolver = new LawnElementResolver(index);

        var (side, elements) = resolver.Resolve("m1", "0xA", () => ("plant", 0, true));

        Assert.Equal("plant", side);
        Assert.Equal(ElementTypeId.Earth, elements.Primary);
        Assert.False(elements.IsNeutral);
        Assert.Equal(1, resolver.CachedPtrCount); // a real species-0 resolve caches normally
    }

    [Fact]
    public void Secondary_equal_to_primary_collapses_to_null_matching_BattleEngine_exactly()
    {
        // CreatureSpeciesCatalog.Validate already refuses this at import, so this is belt-and-braces —
        // spec-lawn-element-bind.md §2.4 asks for it anyway: the two runtimes must construct
        // identically, corner case included, or they drift apart the way they did before E27.
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10, ElementTypeId.Fire, secondary: ElementTypeId.Fire),
        });
        var resolver = new LawnElementResolver(index);

        var (_, elements) = resolver.Resolve("m1", "0xA", () => ("plant", 10, true));

        Assert.Equal(ElementTypeId.Fire, elements.Primary);
        Assert.Null(elements.Secondary);
    }

    [Fact]
    public void A_real_secondary_survives()
    {
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10, ElementTypeId.Fire, secondary: ElementTypeId.Ice),
        });
        var resolver = new LawnElementResolver(index);

        var (_, elements) = resolver.Resolve("m1", "0xA", () => ("plant", 10, true));

        Assert.Equal(ElementTypeId.Fire, elements.Primary);
        Assert.Equal(ElementTypeId.Ice, elements.Secondary);
    }

    // ---- absent is Neutral, never a throw, never a guess ----------------------------------------------

    [Fact]
    public void No_species_for_the_pair_resolves_Neutral_not_a_throw()
    {
        var index = new LawnElementIndex(Array.Empty<CreatureSpeciesDef>());
        var resolver = new LawnElementResolver(index);

        var (_, elements) = resolver.Resolve("m1", "0xA", () => ("plant", 404, true));

        Assert.True(elements.IsNeutral);
    }

    [Fact]
    public void A_miss_is_reported_once_per_typeId_per_match_not_once_per_actor()
    {
        var reports = new List<string>();
        var index = new LawnElementIndex(Array.Empty<CreatureSpeciesDef>());
        var resolver = new LawnElementResolver(index, reports.Add);

        resolver.Resolve("m1", "0xA", () => ("plant", 404, true));
        resolver.Resolve("m1", "0xB", () => ("plant", 404, true)); // different actor, same typeId
        resolver.Resolve("m1", "0xC", () => ("plant", 405, true)); // a different miss

        Assert.Equal(2, reports.Count);
    }

    [Fact]
    public void Planted_violation_an_undefined_ElementPrimary_resolves_Neutral_and_reports()
    {
        // Simulates corrupted/unparseable import data: a species row whose ElementPrimary is not one
        // of the six defined ElementTypeId members. ActorElementTypes.Create performs no such check
        // itself (an enum cast is never range-checked at runtime), so the resolver owns this guard.
        var bad = Species("s1", "plant", 10) with { ElementPrimary = (ElementTypeId)99 };
        var index = new LawnElementIndex(new[] { bad });
        var reports = new List<string>();
        var resolver = new LawnElementResolver(index, reports.Add);

        var (_, elements) = resolver.Resolve("m1", "0xA", () => ("plant", 10, true));

        Assert.True(elements.IsNeutral);
        Assert.Single(reports);
        Assert.Contains("s1", reports[0]);
    }

    // ---- the cache: once per actor per match, cleared on match change ---------------------------------

    [Fact]
    public void A_repeat_resolve_for_the_same_actor_in_the_same_match_never_calls_boardLookup_again()
    {
        var index = new LawnElementIndex(new[] { Species("s1", "plant", 10) });
        var resolver = new LawnElementResolver(index);
        var lookups = 0;

        (string, int, bool) BoardLookup() { lookups++; return ("plant", 10, true); }

        resolver.Resolve("m1", "0xA", BoardLookup);
        resolver.Resolve("m1", "0xA", BoardLookup);
        resolver.Resolve("m1", "0xA", BoardLookup);

        Assert.Equal(1, lookups);
        Assert.Equal(3, resolver.ResolveCallCount);
        Assert.Equal(1, resolver.BoardLookupCount);
    }

    [Fact]
    public void A_different_actor_in_the_same_match_gets_its_own_board_lookup()
    {
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10),
            Species("s2", "zombie", 20),
        });
        var resolver = new LawnElementResolver(index);

        resolver.Resolve("m1", "0xA", () => ("plant", 10, true));
        resolver.Resolve("m1", "0xB", () => ("zombie", 20, true));

        Assert.Equal(2, resolver.BoardLookupCount);
    }

    [Fact]
    public void A_match_key_change_clears_the_cache_a_pointer_can_be_reused_by_a_different_entity()
    {
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10, ElementTypeId.Fire),
            Species("s2", "zombie", 20, ElementTypeId.Ice),
        });
        var resolver = new LawnElementResolver(index);

        var first = resolver.Resolve("m1", "0xA", () => ("plant", 10, true));
        Assert.Equal(ElementTypeId.Fire, first.Elements.Primary);

        // Same pointer, a new match, a different entity behind it — must NOT reuse m1's cached fire.
        var second = resolver.Resolve("m2", "0xA", () => ("zombie", 20, true));

        Assert.Equal(ElementTypeId.Ice, second.Elements.Primary);
        Assert.Equal(2, resolver.BoardLookupCount);
    }

    [Fact]
    public void A_miss_reported_in_one_match_is_reported_again_in_the_next()
    {
        // The report-once dedup is per match, not process-lifetime — a genuinely missing species stays
        // visible across every match it recurs in, not just the first.
        var reports = new List<string>();
        var index = new LawnElementIndex(Array.Empty<CreatureSpeciesDef>());
        var resolver = new LawnElementResolver(index, reports.Add);

        resolver.Resolve("m1", "0xA", () => ("plant", 404, true));
        resolver.Resolve("m2", "0xA", () => ("plant", 404, true));

        Assert.Equal(2, reports.Count);
    }

    // ---- DESIGN-GATE §2.16: the full trigger set ------------------------------------------------------
    //
    // This cache is populated on one trigger (a resolve) and describes state that changes on others.
    // §2.16 requires the enumeration of EVERY edge that can change what a ptr resolves to, with a test
    // per edge — the enumeration is the deliverable, not just whichever edge prompted the work. There
    // are now five candidates; three fire (one added 2026-09-14) and two provably cannot, checked below.
    //
    //   1 match change          fires — wholesale clear (A_match_key_change_clears_the_cache..., above)
    //   2 hypno / charm         cannot fire — `side` is object kind, not allegiance
    //   3 death + ptr reuse     fires — per-ptr Invalidate, wired at GameHooks.ForgetEntity
    //   4 catalog revision      cannot fire — the roster is configured once per host at startup
    //   5 board-miss (Found)    fires — the ptr's own board-registration racing this resolve must never
    //                            latch a Neutral answer (see Trigger5 tests below)

    // ---- trigger 5: the board not knowing this ptr YET (added 2026-09-14) ----------------------------
    //
    // lawn-combat-wire T10/T12 live-inert investigation, second defect: a `LawnBasicAttackGrantBinder`
    // grant, bound once at spawn, resolves the actor's element via THIS resolver keyed on
    // `boardLookup() -> Found`. Confirmed live (POST /api/debug/effect/list against a real running
    // game): roughly half of otherwise-identical `debug.spawn-plant`/`debug.spawn-zombie` calls lost a
    // real, reproducible race — the grant-bind drain ran before `InjectorEntityRegistry.Add` had
    // registered the freshly-spawned entity, so `boardLookup()` answered `Found = false`. Before this
    // fix, that miss was cached exactly like any other miss — permanently, for the ptr — so
    // `LawnBasicAttackGrantBinder`'s own requeue-and-retry (added the same day, once the entity WAS
    // registered a frame or two later) still read back the stale Neutral answer forever, because a
    // cache hit never calls `boardLookup` again. The fix: `Found == false` is answered directly and
    // never written into `_cache`.
    //
    // 2026-09-14, corrected same day: the original shape of this fix used `typeId == 0` as the miss
    // signal instead of a real `Found` flag — wrong for this game build, where species index 0
    // (Peashooter, NormalZombie) is a real creature, not a sentinel (see
    // `A_species_at_gameTypeId_zero_resolves_normally...`, above, and `LawnElementResolverHost`'s own
    // doc). The tests below now drive `Found` directly and use a nonzero typeId so a regression back to
    // the `typeId == 0` shape would not accidentally pass them.

    [Fact]
    public void Trigger5_a_board_miss_is_never_cached_so_a_later_real_resolve_still_finds_the_species()
    {
        var index = new LawnElementIndex(new[] { Species("s1", "plant", 10, ElementTypeId.Fire) });
        var resolver = new LawnElementResolver(index);

        // First call: the board does not know this ptr yet (registry hasn't caught up with the spawn).
        var early = resolver.Resolve("m1", "1A2B", () => ("plant", 10, false));
        Assert.True(early.Elements.IsNeutral);

        // Second call, SAME ptr, SAME match: the entity is registered now. Without the fix this would
        // still read back the cached Neutral from the first call and never invoke boardLookup again —
        // exactly the live defect (grants:0 forever after a lost race, confirmed via a real running game).
        var later = resolver.Resolve("m1", "1A2B", () => ("plant", 10, true));

        Assert.Equal(ElementTypeId.Fire, later.Elements.Primary);
        Assert.False(later.Elements.IsNeutral);
        Assert.Equal(2, resolver.BoardLookupCount); // both calls actually asked the board — no cache hit
    }

    [Fact]
    public void Trigger5_a_board_miss_does_not_count_as_a_cached_ptr()
    {
        var index = new LawnElementIndex(new[] { Species("s1", "plant", 10) });
        var resolver = new LawnElementResolver(index);

        resolver.Resolve("m1", "1A2B", () => ("plant", 10, false));

        // Nothing was ever cached for this ptr -- CachedPtrCount only counts entries a later resolve
        // could read back stale, and a board-miss must never be one of them.
        Assert.Equal(0, resolver.CachedPtrCount);
    }

    [Fact]
    public void Trigger5_a_genuine_content_gap_found_true_no_species_still_caches_as_before()
    {
        // The negative control: this fix narrows the no-cache rule to Found == false specifically. A
        // real entity whose type the species catalog simply does not cover (Found == true) is a
        // DIFFERENT, pre-existing case (`No_species_for_the_pair_resolves_Neutral_not_a_throw`, above)
        // and must keep caching + reporting exactly as before -- this fix must not accidentally widen
        // into "never cache any Neutral", which would silently reintroduce the per-hit board-scan cost
        // E27 removed.
        var index = new LawnElementIndex(Array.Empty<CreatureSpeciesDef>());
        var resolver = new LawnElementResolver(index);

        resolver.Resolve("m1", "1A2B", () => ("plant", 404, true)); // a real entity, just not in the catalog

        Assert.Equal(1, resolver.CachedPtrCount);
        resolver.Resolve("m1", "1A2B", () => throw new InvalidOperationException("must still be cached"));
    }

    // ---- trigger 3: death + IL2CPP pointer reuse ------------------------------------------------------

    [Fact]
    public void Trigger3_a_pointer_reused_by_a_new_entity_in_the_same_match_does_not_inherit_the_dead_ones_element()
    {
        // The executable form of the spec's success criterion: resolve P -> Fire; the entity dies
        // (ForgetEntity -> Invalidate); a DIFFERENT species is allocated at the same address; resolve P
        // must not still answer Fire. Same matchKey throughout — the match-change clear cannot save us
        // here, which is exactly why this edge needed its own invalidation.
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10, ElementTypeId.Fire),
            Species("s2", "zombie", 20, ElementTypeId.Ice),
        });
        var resolver = new LawnElementResolver(index);

        var before = resolver.Resolve("m1", "1A2B", () => ("plant", 10, true));
        Assert.Equal(ElementTypeId.Fire, before.Elements.Primary);
        Assert.Equal("plant", before.Side);

        Assert.True(resolver.Invalidate("1A2B"));

        var after = resolver.Resolve("m1", "1A2B", () => ("zombie", 20, true));

        Assert.NotEqual(ElementTypeId.Fire, after.Elements.Primary);
        Assert.Equal(ElementTypeId.Ice, after.Elements.Primary);
        Assert.Equal("zombie", after.Side);
        Assert.Equal(2, resolver.BoardLookupCount);
    }

    [Fact]
    public void Trigger3_without_the_invalidation_the_reused_pointer_would_have_inherited_the_dead_entity()
    {
        // The negative control. Without it the test above could pass for the wrong reason (e.g. if the
        // cache had quietly stopped caching), and the defect it guards would be invisible.
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10, ElementTypeId.Fire),
            Species("s2", "zombie", 20, ElementTypeId.Ice),
        });
        var resolver = new LawnElementResolver(index);

        resolver.Resolve("m1", "1A2B", () => ("plant", 10, true));
        var stale = resolver.Resolve("m1", "1A2B", () => ("zombie", 20, true)); // no Invalidate

        Assert.Equal(ElementTypeId.Fire, stale.Elements.Primary);
        Assert.Equal(1, resolver.BoardLookupCount);
    }

    [Fact]
    public void Invalidate_removes_exactly_one_entry_never_the_whole_cache()
    {
        // The spec's "no whole-cache clear on any per-entity path" criterion. A clear-on-death would
        // re-run boardLookup for every actor on the board, which is the per-hit board-scan cost this
        // cache exists to remove.
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10),
            Species("s2", "zombie", 20),
            Species("s3", "plant", 30),
        });
        var resolver = new LawnElementResolver(index);

        resolver.Resolve("m1", "A", () => ("plant", 10, true));
        resolver.Resolve("m1", "B", () => ("zombie", 20, true));
        resolver.Resolve("m1", "C", () => ("plant", 30, true));
        Assert.Equal(3, resolver.CachedPtrCount);
        Assert.Equal(3, resolver.BoardLookupCount);

        resolver.Invalidate("B");

        Assert.Equal(2, resolver.CachedPtrCount);

        // A and C still hit; only B pays a second board lookup.
        resolver.Resolve("m1", "A", () => throw new InvalidOperationException("A must still be cached"));
        resolver.Resolve("m1", "C", () => throw new InvalidOperationException("C must still be cached"));
        resolver.Resolve("m1", "B", () => ("zombie", 20, true));

        Assert.Equal(4, resolver.BoardLookupCount);
    }

    [Theory]
    [InlineData("never-cached")]
    [InlineData("")]
    [InlineData(null)]
    public void Invalidate_of_an_unknown_or_empty_pointer_is_a_no_op_never_a_throw(string? ptr)
    {
        // The caller is a death hook that fires for EVERY entity, including ones no element read ever
        // touched, so "not cached" is the normal case rather than an error.
        var index = new LawnElementIndex(new[] { Species("s1", "plant", 10) });
        var resolver = new LawnElementResolver(index);
        resolver.Resolve("m1", "A", () => ("plant", 10, true));

        Assert.False(resolver.Invalidate(ptr));
        Assert.Equal(1, resolver.CachedPtrCount);
    }

    [Fact]
    public void Invalidate_hits_whatever_spelling_of_the_pointer_the_caller_used()
    {
        // The three production call sites disagree on ptr spelling — GateCounterHost passes
        // CombatPtr.Normalize(ptr) while both combat bridges pass the raw key — so a cache keyed on the
        // caller's literal string would hold two entries per entity and an invalidation would clear one
        // and leave the other serving the dead entity. The key is canonical for exactly this reason.
        var index = new LawnElementIndex(new[]
        {
            Species("s1", "plant", 10, ElementTypeId.Fire),
            Species("s2", "zombie", 20, ElementTypeId.Ice),
        });
        var resolver = new LawnElementResolver(index);

        resolver.Resolve("m1", "0x1A2B", () => ("plant", 10, true));
        var sameEntity = resolver.Resolve("m1", "1a2b", () => throw new InvalidOperationException(
            "0x1A2B and 1a2b are the same entity and must share one cache entry"));
        Assert.Equal(ElementTypeId.Fire, sameEntity.Elements.Primary);
        Assert.Equal(1, resolver.CachedPtrCount);

        // GameHooks.ForgetEntity invalidates with ptr.ToString("X") — upper, unprefixed.
        Assert.True(resolver.Invalidate("1A2B"));

        var reused = resolver.Resolve("m1", "0x1A2B", () => ("zombie", 20, true));
        Assert.Equal(ElementTypeId.Ice, reused.Elements.Primary);
    }

    [Fact]
    public void Trigger3_the_injector_leave_board_cleanup_actually_calls_the_invalidation()
    {
        // Core can prove the resolver forgets a ptr, but not that the game ever asks it to — and an
        // invalidation nothing calls is the same defect with extra code. GameHooks.ForgetEntity is the
        // single cleanup every death path funnels through (both the plant and zombie die postfixes call
        // it after their Emit), so this asserts the wiring lives in that method and cannot be dropped
        // silently. Structural, not a population count: it names one call in one method.
        var forgetEntity = MethodBody(
            ReadInjectorFile("GameHooks.cs"), "public static void ForgetEntity(IntPtr ptr)");

        Assert.Contains("LawnElementResolverHost.Invalidate", forgetEntity);
    }

    // ---- trigger 2: hypno / charm — cannot fire -------------------------------------------------------

    [Fact]
    public void Trigger2_hypno_cannot_change_a_cached_side_because_side_is_object_kind_not_allegiance()
    {
        // The premise "a hypnotised zombie changes side, so its cache entry goes stale" does not hold
        // against the code, and this is the check rather than prose. The board snap's `Side` is written
        // as a literal per collection — plants in the plant loop, zombies in the zombie loop — and mind
        // control rides the SEPARATE MindControlled flag. So a charmed zombie's (side, typeId) pair,
        // which is all this cache keys its species lookup on, is unchanged by SetMindControl.
        var registry = ReadInjectorFile("Effects", "InjectorEntityRegistry.cs");

        Assert.Contains("Side = \"plant\"", registry);
        Assert.Contains("Side = \"zombie\"", registry);
        Assert.Contains("MindControlled = mc", registry);

        // ...and nothing in the registry derives Side from the control flag.
        Assert.DoesNotContain("Side = mc", registry);
        Assert.DoesNotContain("isMindControlled ?", registry);
    }

    [Fact]
    public void Trigger2_allegiance_is_derived_from_the_MindControlled_flag_not_from_the_cached_side()
    {
        // MechanicalOwnSideOracle is the SSOT for "which side is this unit fighting for". It folds the
        // flag in at read time, on top of the object-kind side — which is precisely why this cache must
        // NOT flip its stored side on hypno: doing so would double-apply the flip for the oracle and,
        // worse, make the (side, gameTypeId) species lookup miss for every charmed zombie.
        var oracle = ReadCoreFile("Battle", "MechanicalOwnSideOracle.cs");

        Assert.Contains("entity.MindControlled ? Opposite(entity.Side) : entity.Side", oracle);
    }

    [Fact]
    public void Trigger2_the_hypno_capture_site_does_not_invalidate_and_says_why()
    {
        // A no-op needs a reason recorded where the next reader stands, or the "missing" invalidation
        // gets added back by whoever notices it next.
        var capture = ReadInjectorFile("GameCaptureHooks.cs");
        var mindControl = capture[capture.IndexOf("class ZombieMindControl", StringComparison.Ordinal)..];
        var block = mindControl[..mindControl.IndexOf("static void EmitZombieStatus", StringComparison.Ordinal)];

        Assert.DoesNotContain("LawnElementResolverHost.Invalidate", block);
        Assert.Contains("Deliberately does NOT invalidate", capture);
    }

    // ---- trigger 4: catalog revision mid-run — cannot fire --------------------------------------------

    [Fact]
    public void Trigger4_the_species_roster_is_configured_once_per_host_at_startup_never_mid_match()
    {
        // The element index is built once from CreatureSpeciesCatalog, so a mid-run catalog revision
        // would silently stale the whole cache. It cannot happen: the catalog is documented and built
        // as load-once-immutable, and the injector host configures it exactly once, at startup.
        var snapshot = ReadCoreFile("Creatures", "SpeciesSnapshot.cs");
        Assert.Contains("immutable for the process lifetime", snapshot);
        Assert.Contains("No live reload", snapshot);

        var host = ReadInjectorFile("Host", "RpgHost.cs");
        Assert.Contains("CreatureSpeciesCatalog.Configure", host);
    }

    [Fact]
    public void Trigger4_reforge_world_is_gone_so_it_cannot_touch_species_rows_or_the_catalog()
    {
        // `reforge-world` used to be the one mid-run operation that bumped anything called a "catalog
        // revision", which is why the spec named it. `species-progression` SP0.6 removed the whole
        // route (its only content was the eager species reforge, SP0.4's C2 ruling made it
        // illegitimate to keep) — a STRONGER guarantee than "does not touch species rows": there is no
        // handler left to touch anything, so the process-wide species roster the element index is
        // built from cannot move under a route that no longer exists.
        var endpoints = ReadRepoFile("src", "FusionRpg.Server", "DebugEndpoints.cs");

        Assert.DoesNotContain("\"/reforge-world\"", endpoints);
        // The exact call syntax, not any textual mention — the file's own retirement comment names the
        // method in prose (why it was removed), which is documentation, never a live call site.
        Assert.DoesNotContain(".ReforgePlayerSpecies(", endpoints);
    }

    // ---- source reading (the ElementHubDocDriftTests pattern) -----------------------------------------

    static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing signature: " + signature);

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, "no body for: " + signature);

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }

        throw new InvalidOperationException("unbalanced body for: " + signature);
    }

    static string ReadInjectorFile(params string[] relative) =>
        ReadRepoFile(new[] { "src", "FusionRpg.Injector" }.Concat(relative).ToArray());

    static string ReadCoreFile(params string[] relative) =>
        ReadRepoFile(new[] { "src", "FusionRpg.Core" }.Concat(relative).ToArray());

    static string ReadRepoFile(params string[] relative)
    {
        var path = Path.Combine(new[] { FindRepoRoot() }.Concat(relative).ToArray());
        Assert.True(File.Exists(path), "missing " + path);
        return File.ReadAllText(path);
    }

    static string FindRepoRoot()
    {
        return KeepverseRoots.Core();
    }
}
