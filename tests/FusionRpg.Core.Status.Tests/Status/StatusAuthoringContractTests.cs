using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.ActorSurface;
using FusionRpg.Core.Combat;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;
using FusionRpg.Core.Workspace;
using Xunit;
using CoreStatus = FusionRpg.Core.Status;

namespace FusionRpg.Core.Tests.Status;

/// <summary>
/// status-tracks `combat-track-authoring` — the authoring contract for a COMBAT status: the three
/// registration points that must agree, and the grant-overlay keys the apply path actually reads.
///
/// <para><b>Ships no new status id.</b> Both halves are proven with a THROWAWAY id constructed
/// in memory and discarded at the end of the test: a real id in a shipped file would permanently
/// widen a vocabulary this program documents as closed and reviewed.</para>
///
/// <para><b>What each half refuses.</b> Registration drift — a row in one file and not another — used
/// to be a thing you found by reading three files side by side. Overlay drift is worse: an unknown
/// key <i>throws</i> at grant time (<c>EffectBag.Grant</c> →
/// <c>EffectOverlayMerge.TryValidateOverlayForDef</c>), so a typo is loud, but a documented key that
/// nothing reads is silent and looks authored. Both are contracts, so both are tests.</para>
/// </summary>
public class StatusAuthoringContractTests
{
    // A throwaway id. It is deliberately shaped like a real one (a `neuro.`-style dotted id, the
    // shape `nerve.*` proved a status id may take) and deliberately NOT one of the locked 24, so it
    // can be proven absent from every shipped registry without any of them being edited.
    const string Probe = "neuro.contract_probe";

    // A second, never-registered name. `Register` is ADDITIVE with no remove (it is an extension
    // seam, not a map with a delete), so `Probe` stays in the process-wide dictionary for the rest
    // of the run once a test has planted it — and xUnit does not order tests within a class. Any
    // assertion about "an id the registry has never heard of" must therefore name an id that no
    // test ever writes, not `Probe`.
    const string Unregistered = "neuro.never_registered";

    static string CatalogJson => File.ReadAllText(
        Path.Combine(KeepverseRoots.Core(), "data", "tuning", "status-catalog.v1.json"));

    // ==========================================================================================
    // ST1.1 — the three registration points
    // ==========================================================================================

    /// <summary>
    /// The drift detector, proven by planting the drift. Everything below this test only asserts
    /// agreement; this one asserts that <b>disagreement is visible</b>, which is the half a
    /// "both are 24" assertion can never prove.
    ///
    /// <para>What is planted, and why each piece is enough on its own:</para>
    /// <list type="bullet">
    /// <item>the registry gains <see cref="Probe"/> through the sanctioned additive seam
    /// (<c>StatusCategoryRegistry.Register</c>) — the exact call an exhaustion/stance id makes;</item>
    /// <item>the bootstrap golden gains a def for it;</item>
    /// <item>the INJECTED catalog (built from <c>status-catalog.v1.json</c>) does NOT.</item>
    /// </list>
    ///
    /// <para>So the two surfaces genuinely disagree, and the same element-wise comparison
    /// <c>StatusCatalogParityTests.Json_entry_ids_equal_Bootstrap_ids</c> performs fails on it. The
    /// injected catalog is what a configured host actually runs
    /// (<c>StatusCatalogHub.Current</c> → <c>StatusCatalogFactory.FromSurface</c>), which is the half
    /// that matters: a status that resolves in tests and is absent in play.</para>
    ///
    /// <para>Restored in a <c>finally</c>: the registry is process-wide static state
    /// (<c>StatusCategoryRegistry.Map</c>), and this assembly disables test parallelisation but not
    /// test ORDER. Leaving the probe registered would make
    /// <c>SingleDeclarationTests.AllStatusIds_is_the_locked_twenty_four_member_set</c> in another
    /// project fail for a reason this test caused — which is the difference between proving a
    /// contract and corrupting one.</para>
    /// </summary>
    [Fact]
    public void An_id_in_the_registry_and_bootstrap_but_not_the_json_is_visible_as_parity_drift()
    {
        try
        {
            CoreStatus.StatusCategoryRegistry.Register(Probe, CoreStatus.StatusL2bCategory.Dot);
            var bootstrap = ProbeCatalog();                      // golden + the planted id
            var injected = CoreStatus.StatusCatalogFactory.FromSurface(
                StatusSurfaceCatalogLoader.Parse(CatalogJson));  // the shipped JSON, unplanted

            // Precondition: the plant actually landed on both surfaces it claimed, so a later
            // "parity would have failed" cannot be true merely because nothing happened.
            Assert.Contains(Probe, bootstrap.All().Select(d => d.StatusId));
            Assert.Contains(Probe, CoreStatus.StatusCategoryRegistry.AllStatusIds);
            Assert.DoesNotContain(Probe, injected.All().Select(d => d.StatusId));

            var bootstrapIds = bootstrap.All().Select(d => d.StatusId)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            var jsonIds = injected.All().Select(d => d.StatusId)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

            // The parity assertion, reproduced. `Assert.NotEqual` is the honest form here: the
            // contract is that these two sequences EQUAL, and the planted probe makes them not.
            Assert.NotEqual(bootstrapIds, jsonIds);
        }
        finally
        {
            UnregisterProbe();
        }
    }

    /// <summary>
    /// The positive half: an id added to all THREE is accepted by every guard, at the same time,
    /// with no shipped file edited. This is the state an author reaches after finishing the change —
    /// so it is the state worth pinning, and it is what makes the drift test above meaningful
    /// rather than merely a subtraction.
    ///
    /// <para>Each guard is replayed here against the PLANTED row, not merely assumed to tolerate it:
    /// </para>
    /// <list type="bullet">
    /// <item>registry → bootstrap: bootstrap reads its category from the registry, so a def whose id
    /// the registry has never heard of throws at construction
    /// (<c>StatusCatalogBootstrap.Register</c> → <c>StatusCategoryRegistry.GetRequiredCategory</c>);
    /// asserting the planted def comes back with the registry's own category proves the ownership
    /// direction, not a coincidence;</item>
    /// <item>the JSON row parses and injects: <c>StatusSurfaceCatalogLoader.Parse</c> is a closed
    /// parser — an unknown <c>kind</c>/<c>stacking</c>/<c>payloadKinds</c> name is a load rejection,
    /// not a default — so a row that parses is a row whose vocabularies all exist;</item>
    /// <item>the two catalogs agree, element-wise, which is the parity assertion passing;</item>
    /// <item>the count guards: both counts are the roster's own size, so a real id moves them. Named
    /// here rather than hard-coded so the assertion below can check they still exist where this
    /// document says they do.</item>
    /// </list>
    /// </summary>
    [Fact]
    public void An_id_added_to_all_three_lands_without_editing_a_shipped_file()
    {
        try
        {
            CoreStatus.StatusCategoryRegistry.Register(Probe, CoreStatus.StatusL2bCategory.Dot);
            var bootstrap = ProbeCatalog();
            var injected = CoreStatus.StatusCatalogFactory.FromSurface(
                StatusSurfaceCatalogLoader.Parse(PlantedCatalogJson()));

            // Guard 1 -- registry owns the category; the bootstrap cannot declare its own.
            var def = bootstrap.GetRequired(Probe);
            Assert.Equal(
                new[] { CoreStatus.StatusL2bCategory.Dot },
                def.Categories.ToArray());
            Assert.Equal(
                CoreStatus.StatusCategoryRegistry.GetRequiredCategory(Probe),
                def.Categories[0]);

            // Guard 2 -- the JSON row parses through the closed loader (unknown enum names reject).
            var row = StatusSurfaceCatalogLoader.Parse(PlantedCatalogJson())
                .Entries.Single(e => e.Id == Probe);
            Assert.Equal(CoreStatus.StatusKind.Debuff, row.Kind);
            Assert.Contains(CoreStatus.StatusPayloadKind.ModifyStat, injected.GetRequired(Probe).PayloadKinds);

            // Guard 3 -- parity, element-wise, exactly as StatusCatalogParityTests asserts it.
            var bootstrapIds = bootstrap.All().Select(d => d.StatusId)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            var jsonIds = injected.All().Select(d => d.StatusId)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
            Assert.Equal(bootstrapIds, jsonIds);

            foreach (var id in bootstrap.All().Select(d => d.StatusId))
            {
                var a = injected.GetRequired(id);
                var b = bootstrap.GetRequired(id);
                Assert.Equal(b.Kind, a.Kind);
                Assert.Equal(b.Stacking, a.Stacking);
                Assert.Equal(b.Family, a.Family);
                Assert.Equal(b.PayloadKinds.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToArray(),
                    a.PayloadKinds.OrderBy(k => k.ToString(), StringComparer.Ordinal).ToArray());
                Assert.Equal(b.Categories.OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                    a.Categories.OrderBy(c => c, StringComparer.Ordinal).ToArray());
            }

            // Guard 4 -- the roster grew by exactly the probe, so both count guards would move with
            // it. Asserted as a DELTA against the shipped size rather than as a population count:
            // what is under test is "a new id moves the counts", not "the number is 24".
            var shipped = CoreStatus.StatusCatalogBootstrap.CreateDefault().All().Count;
            Assert.Equal(shipped + 1, bootstrap.All().Count);
            Assert.Equal(shipped + 1, injected.All().Count);
        }
        finally
        {
            UnregisterProbe();
        }
    }

    /// <summary>
    /// The counts are not restated here. They live in two other files, this document cites them by
    /// name, and a rename would leave every citation in this document quietly pointing at nothing
    /// while the suite stayed green — so their existence is asserted instead.
    ///
    /// <para>Read as source text, not by running the other projects: a test that needs
    /// <c>FusionRpg.Core.Vocabulary.Tests</c> compiled to prove a name still exists has turned a
    /// citation into a build dependency, which is exactly the coupling the separation is for. The
    /// files themselves are the evidence; this only checks the names are where they are said to be,
    /// and that each really is the guard its name claims (a <c>[Fact]</c> method asserting a count,
    /// not a helper).</para>
    /// </summary>
    [Fact]
    public void The_two_count_guards_still_live_where_this_document_says_they_do()
    {
        var vocabulary = TestSource(
            "tests", "FusionRpg.Core.Vocabulary.Tests", "Vocabulary", "SingleDeclarationTests.cs");
        var status = TestSource(
            "tests", "FusionRpg.Core.Status.Tests", "Status", "ResistanceEvaluatorTests.cs");

        // Assert.Equal(expected, StatusCategoryRegistry.AllStatusIds
        //   ...), i.e. a pinned SET, not a count -- so the id-set assertion and its method are the
        //   same fact, and a rename that left only the name behind would be caught.
        var setPin = Declaring(vocabulary, "public void AllStatusIds_is_the_locked_twenty_four_member_set");
        Assert.True(setPin.HasValue, "SingleDeclarationTests.AllStatusIds_is_the_locked_twenty_four_member_set is gone or renamed");
        Assert.Contains("[Fact]", Prior(vocabulary, setPin!.Value));
        Assert.Contains("Assert.Equal(expected, StatusCategoryRegistry.AllStatusIds",
            Rest(vocabulary, setPin.Value, 20));

        // gk-core/tests/FusionRpg.Core.Status.Tests/Status/ResistanceEvaluatorTests.cs:461
        var countPin = Declaring(status, "public void Bootstrap_registers_24_ids()");
        Assert.True(countPin.HasValue, "ResistanceEvaluatorTests.Bootstrap_registers_24_ids is gone or renamed");
        Assert.Contains("[Fact]", Prior(status, countPin!.Value));
        Assert.Contains("Assert.Equal(24, catalog.All().Count);", Rest(status, countPin.Value, 20));
    }

    /// <summary>
    /// The bootstrap is a CLOSED golden: its ids are a subset of the registry's, so an id that
    /// reaches the catalog without a category cannot exist — <c>GetRequiredCategory</c> throws
    /// during <c>RegisterAll</c>, before a def with an unanswerable resist channel is ever built.
    /// And the three-value category vocabulary is closed on the WRITE side too, which is the half
    /// that makes "one failure mode" true rather than aspirational.
    ///
    /// <para><b>Neither half touches the probe.</b> <c>Register</c> has no remove, and every write
    /// to that process-wide dictionary would leak membership into whatever ran next — including
    /// the locked-set assertion in another project. So both facts are read through an id the
    /// registry has never heard of, which is the same predicate <c>RegisterAll</c> reads.</para>
    /// </summary>
    [Fact]
    public void A_bootstrap_id_with_no_registry_category_fails_at_bootstrap_not_at_resist_time()
    {
        Assert.Throws<ArgumentException>(
            () => CoreStatus.StatusCategoryRegistry.GetRequiredCategory(Unregistered));
        Assert.DoesNotContain(Unregistered, CoreStatus.StatusCategoryRegistry.AllStatusIds);
    }

    [Fact]
    public void The_category_vocabulary_is_closed_at_three_on_the_write_side_too()
    {
        // dot / cc / contagion only. Register refuses anything else rather than storing it, so a
        // typo cannot become a fourth resist axis that nothing composes. The refused ids are names
        // that were never valid, so nothing leaks into the process-wide map either way.
        Assert.Throws<ArgumentException>(
            () => CoreStatus.StatusCategoryRegistry.Register("slow_candidate", "debuff"));
        Assert.Throws<ArgumentException>(
            () => CoreStatus.StatusCategoryRegistry.Register("slow_candidate", "slow"));
        Assert.Throws<ArgumentException>(
            () => CoreStatus.StatusCategoryRegistry.Register("", CoreStatus.StatusL2bCategory.Dot));
        Assert.DoesNotContain("slow_candidate", CoreStatus.StatusCategoryRegistry.AllStatusIds);
    }

    // ==========================================================================================
    // ST1.2 -- the authored overlay contract
    // ==========================================================================================

    /// <summary>
    /// Every key <c>StatusEffectBridge.BuildApplyInput</c> reads, in one overlay, with the value
    /// each is proven to reach <c>StatusApplyInput</c>. Fixture shape copied from
    /// <c>StatusEffectBridgeTests.BuildApplyInput_parses_spread_and_immunity_tags</c>; the
    /// difference is that here EVERY documented key is present at once, so a key that stops being
    /// read shows up as a default-value failure here rather than as a silently inert field nobody
    /// notices.
    /// </summary>
    [Fact]
    public void Every_documented_overlay_key_reaches_the_apply_input()
    {
        var input = BuildInput(AllKeysOverlay());

        // Flat scalars.
        Assert.Equal(750, input.PeriodMs);
        Assert.Equal(4500, input.DurationMs);
        Assert.Equal(4500, input.BaseDuration);   // `durationMs` fills BOTH duration slots
        Assert.Equal(-37.0, input.BaseMagnitude);
        Assert.Equal(0.6, input.GrantChance);
        Assert.Equal(3000, input.StatusIcdMs);
        Assert.Equal(4, input.TickBudget);
        Assert.Equal("Z1", input.HostPtr);
        Assert.Equal("P1", input.AttackerPtr);
        Assert.Equal("g-1", input.GrantId);

        // spread.* -- the whole block, including the target spec.
        Assert.Equal(0.4, input.SpreadChance);
        Assert.Equal("rot", input.SpreadStatusId);
        Assert.Equal(3, input.SpreadMaxHops);
        Assert.Equal(1500, input.SpreadIcdMs);
        Assert.NotNull(input.SpreadTarget);
        Assert.Equal(TargetModes.Area, input.SpreadTarget!.Mode);

        // immunityTags -- array form.
        Assert.NotNull(input.ImmunityTags);
        Assert.Equal(new[] { "poison", "dot" }, input.ImmunityTags!.ToArray());

        // stat -- one timed modifier on a channel that composes.
        var mod = Assert.Single(input.StatMods!);
        Assert.Equal("atk", mod.ChannelId);
        Assert.Equal("more", mod.Op);
        Assert.Equal(-0.25, mod.Value);
    }

    /// <summary>The documented defaults, for an overlay that authors none of them.</summary>
    [Fact]
    public void An_overlay_with_no_timing_keys_takes_the_documented_defaults()
    {
        var input = BuildInput(new Dictionary<string, object?> { ["statusId"] = "wither" });

        Assert.Equal(1000, input.PeriodMs);   // StatusEffectBridge.cs:237
        Assert.Equal(5000, input.DurationMs); // StatusEffectBridge.cs:244
        Assert.Equal(5000, input.BaseDuration);
        Assert.Equal(1, input.TickBudget);    // StatusEffectBridge.cs:259
        Assert.Equal(1.0, input.GrantChance); // StatusEffectBridge.cs:250
        Assert.Equal(0, input.StatusIcdMs);   // absent on both spellings -> no ICD gate
        Assert.Equal(0.0, input.BaseMagnitude); // `amount` absent -> 0, and 0 is not re-signed
        Assert.Null(input.SpreadStatusId);
        Assert.Null(input.StatMods);
        Assert.Null(input.ImmunityTags);
    }

    /// <summary>
    /// `status_icd_ms` and `statusIcdMs` are two spellings of ONE gate, not two. Both reach the
    /// same input field, so an author can use either and cannot accidentally open a second clock.
    /// </summary>
    [Fact]
    public void The_two_status_icd_spellings_are_one_gate()
    {
        var snake = BuildInput(new Dictionary<string, object?> { ["status_icd_ms"] = 2500 });
        var camel = BuildInput(new Dictionary<string, object?> { ["statusIcdMs"] = 2500 });

        Assert.Equal(2500, snake.StatusIcdMs);
        Assert.Equal(2500, camel.StatusIcdMs);
        Assert.Equal(snake.StatusIcdMs, camel.StatusIcdMs);
    }

    /// <summary>
    /// The sign rule is NARROWER than "damage unless told otherwise", and the narrowness is the
    /// contract: <c>amount</c> is taken verbatim and only <b>falls back</b> to a forced negative
    /// when it is absent-or-zero. So an explicit <c>amount: 12</c> is a POSITIVE magnitude — a heal
    /// — and authoring a DoT as a positive number silently heals instead. Only the
    /// <c>magnitude</c> alias (which is not on the allowlist, see below) is coerced.
    ///
    /// <para>Pinned because the sign is BEHAVIOURAL — it is what a pulse sends as
    /// <c>DamagePacket.SignedAmount</c> — and no type anywhere carries it.</para>
    /// </summary>
    [Fact]
    public void Magnitude_is_taken_verbatim_and_only_falls_back_to_negative_when_absent()
    {
        // Explicit and negative -- the DoT shape, and the one every shipped row uses.
        Assert.Equal(-12.0, BuildInput(new Dictionary<string, object?> { ["amount"] = -12 }).BaseMagnitude);
        // Explicit and positive -- a heal. NOT flipped.
        Assert.Equal(12.0, BuildInput(new Dictionary<string, object?> { ["amount"] = 12 }).BaseMagnitude);
        // Explicit zero -- kept as zero (the fallback only fires when |amount| < 1e-9, so this is
        // the one input that DOES reach the `magnitude` alias, and an absent alias gives 0).
        Assert.Equal(0.0, BuildInput(new Dictionary<string, object?> { ["amount"] = 0 }).BaseMagnitude);
        // The alias itself: `magnitude` is never negative, because -|x| is how the code says it.
        Assert.Equal(-9.0, BuildInput(new Dictionary<string, object?> { ["magnitude"] = 9 }).BaseMagnitude);
    }

    /// <summary>
    /// `stat` is DROPPED WITH A REASON, not partially applied — and the consumer is optional, so a
    /// block that parses to nothing must not become a half-status that changes no stat and still
    /// occupies an instance slot. Both shapes: an unparseable block, and a well-formed block whose
    /// channels are all unknown.
    /// </summary>
    [Fact]
    public void A_stat_block_that_contributes_nothing_becomes_no_stat_mods()
    {
        var unparseable = BuildInput(new Dictionary<string, object?>
        {
            ["stat"] = Json("""{"atk":"lots"}""")
        });
        Assert.Null(unparseable.StatMods);

        var uncomposed = BuildInput(new Dictionary<string, object?>
        {
            ["stat"] = Json("""{"fireRate":{"flat":1}}""")
        });
        Assert.Null(uncomposed.StatMods);
    }

    /// <summary>
    /// THE trap this program exists to close: an unknown key THROWS at grant time, not at parse and
    /// not silently.
    ///
    /// <para>Proven through the real <c>EffectBag.Grant</c> — not by calling the validator directly —
    /// because the claim under test is *where* the refusal happens. <c>EffectBag.Grant</c> calls
    /// <c>EffectOverlayMerge.TryValidateOverlayForDef</c> UNCONDITIONALLY for every grant, and the
    /// action's own allowlist is the union the overlay is measured against; an action missing from
    /// that dictionary fails even against an EMPTY overlay, which is the fifth instance of that
    /// class of bug recorded at <c>EffectProcAndOwner.cs</c>:300-329.</para>
    ///
    /// <para>The error names the key, because an author who cannot see which key was refused cannot
    /// fix it.</para>
    /// </summary>
    [Fact]
    public void An_unknown_overlay_key_is_refused_at_grant_time_naming_the_key()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => NewBag().Grant(GrantDto(new Dictionary<string, object?>
            {
                ["statusId"] = "wither",
                ["amount"] = -12,
                ["duration"] = 5000
            })));

        Assert.Contains("unknown overlay key 'duration'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative half of the same rule, on the REAL path: every documented key survives the
    /// same allowlist the unknown key just failed. If a key stopped being allowlisted, the refusal
    /// above would stay green while a shipped overlay started throwing — so the two tests are one
    /// contract read from both sides.
    /// </summary>
    [Fact]
    public void Every_documented_overlay_key_survives_the_same_allowlist_that_refused_the_unknown_one()
    {
        foreach (var key in DocumentedKeys)
        {
            var bag = NewBag();
            var overlay = new Dictionary<string, object?> { [key] = ValueFor(key) };
            var grant = bag.Grant(GrantDto(overlay));
            Assert.True(grant.Overlay.ContainsKey(key), $"'{key}' did not reach the stored grant");
        }
    }

    /// <summary>
    /// The documented status keys are exactly ONE action's union, and the two similarly-named
    /// actions are the trap. <c>ApplyResourceDelta</c> carries the whole status vocabulary; FA1
    /// <c>ModifyStat</c> and FA2 <c>ApplyStatus</c> carry their own, narrower, and DIFFERENTLY
    /// SHAPED grammars.
    ///
    /// <para>The sharp edge is <c>stat</c>: it is not "the stat-modifier key" in general, it is a
    /// <c>{"channel": {"op": value}}</c> BLOCK, and only <c>ApplyResourceDelta</c> accepts that
    /// shape. FA1 <c>ModifyStat</c> accepts the same three ops as FLAT TOP-LEVEL keys
    /// (<c>flat</c>/<c>increased</c>/<c>more</c>) beside its own <c>channel</c> — so the intuitive
    /// move of attaching a <c>stat</c> block to a <c>ModifyStat</c> row is refused. Refused loudly,
    /// which is the contract working: an author finds out at load rather than shipping a modifier
    /// nothing composes.</para>
    /// </summary>
    [Fact]
    public void The_documented_keys_belong_to_the_apply_resource_delta_union_and_not_to_every_action()
    {
        var overlayDamage = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = EffectActions.ApplyResourceDelta, Params = new() }
        };

        foreach (var key in DocumentedKeys)
        {
            var ok = EffectOverlayMerge.TryValidateOverlayForDef(
                overlayDamage, new Dictionary<string, object?> { [key] = ValueFor(key) }, out var error);
            Assert.True(ok, $"ApplyResourceDelta refused documented key '{key}': {error}");
        }

        // The trap, both directions.
        var modifyStat = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = EffectActions.ModifyStat, Params = new() }
        };
        var statOnFa1 = EffectOverlayMerge.TryValidateOverlayForDef(
            modifyStat, new Dictionary<string, object?> { ["stat"] = Json("""{"atk":{"more":-0.1}}""") },
            out var fa1Error);
        Assert.False(statOnFa1);
        Assert.Contains("unknown overlay key 'stat'", fa1Error!, StringComparison.Ordinal);

        // ...while FA1's OWN stat grammar is still accepted, so the refusal above is about the
        // shape and not about stat modification being illegal there.
        Assert.True(EffectOverlayMerge.TryValidateOverlayForDef(
            modifyStat,
            new Dictionary<string, object?> { ["channel"] = "atk", ["more"] = -0.1 },
            out var fa1Own));

        // And FA2 `ApplyStatus` speaks its own vocabulary (`status`, not `statusId`).
        var applyStatus = new List<EffectActionRow>
        {
            new() { Seq = 1, Action = EffectActions.ApplyStatus, Params = new() }
        };
        Assert.False(EffectOverlayMerge.TryValidateOverlayForDef(
            applyStatus, new Dictionary<string, object?> { ["statusId"] = "wither" }, out var fa2Error));
        Assert.Contains("unknown overlay key 'statusId'", fa2Error!, StringComparison.Ordinal);
        Assert.True(EffectOverlayMerge.TryValidateOverlayForDef(
            applyStatus, new Dictionary<string, object?> { ["status"] = "wither", ["duration"] = 2 }, out _));
    }

    /// <summary>
    /// A documented key that parses is not the same as one that does something. <c>stat</c> on a
    /// status that never declared <c>ModifyStat</c> is refused at apply time with a named reason —
    /// an overlay that validates, stores, and silently changes nothing is the shape E17 and C2 were
    /// both written to close, and it is the failure an author is most likely to produce by copying
    /// a row.
    /// </summary>
    [Fact]
    public void A_stat_key_on_a_status_without_ModifyStat_is_refused_at_apply_time()
    {
        var runtime = Runtime();
        var skipped = new List<string>();
        var grant = new EffectGrant
        {
            GrantId = "g-refuse",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["statusId"] = "blight",
                ["amount"] = -12L,
                ["stat"] = Json("""{"atk":{"more":-0.1}}""")
            }
        };
        var ev = new EffectEventDto { ActorPtr = "P1", TargetPtr = "Z1" };

        var handled = CoreStatus.StatusEffectBridge.TryApplyFromGrant(
            runtime, grant, ev, grant.Overlay, BoardSnapshot.Empty, new CoreStatus.FixedStatusRng(0), Now, skipped);

        Assert.True(handled);
        Assert.Contains(skipped, s => s.EndsWith(":status-stat-overlay-without-ModifyStat", StringComparison.Ordinal));
        Assert.Empty(runtime.ForHost("Z1"));
    }

    /// <summary>
    /// The refusal is about the OVERLAY, not the status: the same block on a status that DOES
    /// declare <c>ModifyStat</c> applies and lands on the instance. Without this half, the test
    /// above would be satisfied by a bridge that dropped <c>stat</c> everywhere.
    /// </summary>
    [Fact]
    public void The_same_block_on_a_status_that_declares_ModifyStat_lands_on_the_instance()
    {
        var runtime = Runtime();
        var skipped = new List<string>();
        var grant = new EffectGrant
        {
            GrantId = "g-ok",
            EffectId = "fx.overlay_damage",
            OwnerKey = EffectOwnerKeys.Match,
            Overlay = new Dictionary<string, object?>
            {
                ["statusId"] = "expose",
                ["amount"] = 0L,
                ["stat"] = Json("""{"defense":{"more":-0.15}}""")
            }
        };
        var ev = new EffectEventDto { ActorPtr = "P1", TargetPtr = "Z1" };

        CoreStatus.StatusEffectBridge.TryApplyFromGrant(
            runtime, grant, ev, grant.Overlay, BoardSnapshot.Empty, new CoreStatus.FixedStatusRng(0), Now, skipped);

        Assert.DoesNotContain(skipped, s => s.Contains("without-ModifyStat", StringComparison.Ordinal));
        var instance = Assert.Single(runtime.ForHost("Z1"));
        Assert.Equal("expose", instance.StatusId);
        var mod = Assert.Single(instance.StatMods);
        Assert.Equal("defense", mod.ChannelId);
        Assert.Equal(-0.15, mod.Value);
    }

    // ==========================================================================================
    // fixtures
    // ==========================================================================================

    /// <summary>
    /// The grant-overlay keys <c>StatusEffectBridge.BuildApplyInput</c> reads. Spelled once and
    /// shared by both directions of the allowlist contract — the list an author works from, and the
    /// list the negative test checks, so the two cannot drift from each other.
    /// </summary>
    static readonly string[] DocumentedKeys =
    {
        "statusId", "periodMs", "durationMs", "amount", "chance",
        "status_icd_ms", "statusIcdMs", "tickBudget", "spread", "immunityTags", "stat"
    };

    /// <summary>Fixed instant, never a read of the wall clock.</summary>
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

    static CoreStatus.StatusRuntime Runtime() =>
        new(CoreStatus.StatusCatalogBootstrap.CreateDefault(),
            (_, attackerLess) => attackerLess
                ? ActorDerivedSnapshot.AttackerLess()
                : ActorDerivedSnapshot.StubNeutral());

    static EffectGrantDto GrantDto(Dictionary<string, object?> overlay) => new()
    {
        GrantId = "g-1",
        EffectId = "fx.overlay_damage",
        OwnerKey = EffectOwnerKeys.Match,
        PluginId = "test",
        Overlay = overlay
    };

    static EffectBag NewBag()
    {
        var catalog = new InMemoryEffectCatalog();
        catalog.ReplaceAll(EffectAtomCatalog.CreateAll());
        var bag = new EffectBag(
            catalog,
            new InMemoryEffectGrantStore(),
            new EffectProcPolicy(new FakeEffectClock(), new SeededEffectRandom(1)),
            new RecordingEffectSink());
        bag.UtcNow = () => Now;
        return bag;
    }

    static EffectGrant BuildInputGrant(Dictionary<string, object?> overlay) => new()
    {
        GrantId = "g-1",
        EffectId = "fx.overlay_damage",
        OwnerKey = EffectOwnerKeys.Match,
        PluginId = "test",
        Overlay = overlay
    };

    static CoreStatus.StatusApplyInput BuildInput(Dictionary<string, object?> overlay) =>
        CoreStatus.StatusEffectBridge.BuildApplyInput(
            CoreStatus.StatusEffectBridge.TryResolveStatusId(overlay, out var id) ? id : "wither",
            BuildInputGrant(overlay),
            new EffectEventDto { ActorPtr = "P1", TargetPtr = "Z1", Tick = 1 },
            overlay,
            "Z1");

    /// <summary>One overlay carrying every key the apply path documents, at a non-default value.</summary>
    static Dictionary<string, object?> AllKeysOverlay() => new()
    {
        ["statusId"] = "blight",
        ["amount"] = -37,
        ["chance"] = 0.6,
        ["periodMs"] = 750,
        ["durationMs"] = 4500,
        ["status_icd_ms"] = 3000,
        ["tickBudget"] = 4,
        ["immunityTags"] = new object[] { "poison", "dot" },
        ["stat"] = Json("""{"atk":{"more":-0.25}}"""),
        ["spread"] = new Dictionary<string, object?>
        {
            ["chance"] = 0.4,
            ["statusId"] = "rot",
            ["maxHops"] = 3,
            ["icd_ms"] = 1500,
            ["target"] = new Dictionary<string, object?>
            {
                ["mode"] = TargetModes.Area,
                ["shape"] = AreaShapes.Row,
                ["anchor"] = "EventTarget"
            }
        }
    };

    /// <summary>A value of the right SHAPE per key — the allowlist reads keys, not values.</summary>
    static object? ValueFor(string key) => key switch
    {
        "stat" => Json("""{"atk":{"more":-0.25}}"""),
        "spread" => new Dictionary<string, object?> { ["chance"] = 0.4 },
        "immunityTags" => new object[] { "dot" },
        "amount" => -12L,
        "chance" => 0.5,
        "statusId" => "wither",
        _ => 1000
    };

    static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    /// <summary>The shipped catalog plus the throwaway row, as text. Never written to disk.</summary>
    static string PlantedCatalogJson()
    {
        using var doc = JsonDocument.Parse(CatalogJson);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray()
            .Select(e => e.GetRawText())
            .ToList();

        entries.Add("""
            {
              "id": "neuro.contract_probe",
              "kind": "Debuff",
              "family": "overlay",
              "categories": ["dot"],
              "stacking": "Replace",
              "payloadKinds": ["ModifyStat"],
              "displayName": "Contract Probe",
              "reading": "Never shipped; exists only for the duration of one test.",
              "hudToken": "?",
              "color": "#888888"
            }
            """);

        var root = new Dictionary<string, object?>
        {
            ["schemaVersion"] = doc.RootElement.GetProperty("schemaVersion").GetInt32(),
            ["version"] = doc.RootElement.GetProperty("version").GetInt32(),
            ["kind"] = "status-catalog",
            ["entries"] = entries.Select(e => JsonDocument.Parse(e).RootElement.Clone()).ToArray()
        };
        return JsonSerializer.Serialize(root);
    }

    /// <summary>The shipped golden plus one def for the probe. In memory; never a shipped file.</summary>
    static CoreStatus.StatusCatalog ProbeCatalog()
    {
        var catalog = CoreStatus.StatusCatalogBootstrap.CreateDefault();
        catalog.Register(new CoreStatus.StatusDef(
            Probe,
            CoreStatus.StatusKind.Debuff,
            "overlay",
            new[] { CoreStatus.StatusCategoryRegistry.GetRequiredCategory(Probe) },
            Array.Empty<string>(),
            CoreStatus.StatusStacking.Replace,
            new[] { CoreStatus.StatusPayloadKind.ModifyStat }));
        return catalog;
    }

    /// <summary>
    /// The registry has no remove, and none should be added: <c>Register</c> is additive by design.
    /// Overwriting with the probe's own category is the closest available restore, and it leaves
    /// membership — the thing the locked-set test pins — untouched.
    /// </summary>
    static void UnregisterProbe() =>
        CoreStatus.StatusCategoryRegistry.Register(Probe, CoreStatus.StatusL2bCategory.Dot);

    // ---- source-text helpers (citations, not build dependencies) --------------------------------

    static string TestSource(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { KeepverseRoots.Core() }.Concat(parts).ToArray()));

    /// <summary>The 1-based line the signature is declared on, or null.</summary>
    static int? Declaring(string source, string signature)
    {
        var lines = source.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (lines[i].Contains(signature, StringComparison.Ordinal))
                return i + 1;
        return null;
    }

    static string Prior(string source, int line) =>
        string.Join('\n', source.Split('\n').Take(line - 1).Reverse().Take(4).Reverse());

    static string Rest(string source, int line, int window = 12) =>
        string.Join('\n', source.Split('\n').Skip(line).Take(window));
}
