using FusionRpg.Core.Items;
using FusionRpg.Core.Items.Thresholds;
using Xunit;
using FusionRpg.Core.Workspace;

namespace FusionRpg.Core.Tests.Items;

/// <summary>
/// `threshold-grants` (item module 12) against the REAL shipped corpora — `gk-data/packs/fusion/data/seed/items/sets/**`
/// (the generated set population), `gk-data/packs/fusion/data/seed/items/charms/**` (the generated charm population +
/// 10 resonance rows) and
/// `gk-data/packs/fusion/data/seed/items/_registry/core.v1.json`. Nothing here is synthetic.
/// </summary>
public class ThresholdGrantCorpusTests
{
    static string ItemsDir() => Path.Combine(KeepverseRoots.Content(), "data", "seed", "items");

    internal static IReadOnlyList<SetDef> Sets() =>
        Directory.EnumerateFiles(Path.Combine(ItemsDir(), "sets"), "*.json")
            .Where(p => !Path.GetFileName(p).EndsWith(".ledger.json", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(p => SetCorpus.Parse(File.ReadAllText(p)))
            .ToList();

    internal static IReadOnlyList<CharmDef> Charms() =>
        Directory.EnumerateFiles(Path.Combine(ItemsDir(), "charms"), "*.json")
            .Where(p => !Path.GetFileName(p).Equals("resonance.json", StringComparison.Ordinal)
                     && !Path.GetFileName(p).Equals("set-charm-gen.ledger.json", StringComparison.Ordinal))
            .OrderBy(p => p, StringComparer.Ordinal)
            .SelectMany(p => CharmCorpus.Parse(File.ReadAllText(p)))
            .ToList();

    internal static IReadOnlyList<CharmResonanceRow> Resonances() =>
        CharmResonance.DeriveTable(File.ReadAllText(Path.Combine(ItemsDir(), "charms", "resonance.json")));

    // ---- the shipped set catalog -------------------------------------------------------------------

    [Fact]
    public void The_whole_shipped_set_corpus_parses_and_every_tier_is_reachable()
    {
        // SetCorpus.Parse refuses a tier above the set's distinct ROLE count, so this parsing at all is
        // the completability assertion. Population size is intentionally not a fixture: the roster
        // generator appends valid set partitions as species are authored.
        var sets = Sets();
        Assert.NotEmpty(sets);
        Assert.All(sets, s => Assert.NotEmpty(s.Members));
        Assert.All(sets, s => Assert.NotEmpty(s.Tiers));
        Assert.All(sets, s => Assert.True(s.Tiers.Max(t => t.PiecesRequired) <= s.DistinctRoleCount));
    }

    [Fact]
    public void Every_shipped_sets_tier_ids_are_derived_padded_and_ordinally_sorted()
    {
        foreach (var set in Sets())
        {
            var ids = set.Tiers.OrderBy(t => t.PiecesRequired).Select(t => t.ContainerId).ToList();
            Assert.Equal(ids, ids.OrderBy(i => i, StringComparer.Ordinal));
            Assert.All(set.Tiers, t => Assert.Equal(ThresholdContainerIds.SetTier(set.SetId, t.PiecesRequired), t.ContainerId));
            Assert.All(ids, id => Assert.Matches(@"^set\.[a-z0-9-]+-[0-9]{2}$", id));
        }
    }

    [Fact]
    public void Every_shipped_set_puts_its_capability_at_the_lowest_threshold()
    {
        // I5 §3.2 / clause 1, module 13's authoring rule — measurable against the corpus that already
        // ships, so this module pins it rather than waiting for the generator to be rewritten.
        foreach (var set in Sets())
        {
            var ordered = set.Tiers.OrderBy(t => t.PiecesRequired).ToList();
            Assert.True(ordered[0].IsCapability, $"{set.SetId}'s lowest tier carries no capability");
            Assert.All(ordered.Skip(1), t => Assert.False(t.IsCapability,
                $"{set.SetId} has a capability above its lowest threshold"));
        }
    }

    [Fact]
    public void The_two_hybrid_themed_set_families_declare_both_frames_on_the_same_role()
    {
        // thorned-chassis and verdant-graft are the hybrid-bodied themes: 8 or 12 members over 4 or 6
        // DISTINCT roles, one per frame. That is exactly the shape UNIQUE (set_id, role, frame) allows
        // and PRIMARY KEY (set_id, container_id) keeps honest — and the reason counting is per role.
        var hybrids = Sets().Where(s => s.SetId.StartsWith("thorned-chassis", StringComparison.Ordinal)
                                        || s.SetId.StartsWith("verdant-graft", StringComparison.Ordinal)).ToList();
        // The member total (12 today) is fully implied by the per-set shape check below -- never
        // pinned separately (population-pin SE3.3, 2026-09-19); this only proves the filter is not
        // vacuous.
        Assert.NotEmpty(hybrids);

        foreach (var set in hybrids)
        {
            Assert.Equal(set.Members.Count, set.DistinctRoleCount * 2);
            foreach (var group in set.Members.GroupBy(m => m.Role))
            {
                Assert.Equal(2, group.Count());
                Assert.Contains(group, m => m.Frame == ItemFrame.Humanoid);
                Assert.Contains(group, m => m.Frame == ItemFrame.Plant);
            }
        }
    }

    [Fact]
    public void A_real_shipped_set_grants_its_real_tiers_as_pieces_go_on()
    {
        // set.frostbitten-vanguard-001: armament-primary / core-guard / jewel-major / footing, with
        // thresholds at 2, 3 and 4. Driven piece by piece over the corpus's own member ids.
        var set = Sets().Single(s => s.SetId == "frostbitten-vanguard-001");
        var consumer = SetEvaluator.Consumer(set);
        var order = set.Members.OrderBy(m => ItemRoles.Id(m.Role), StringComparer.Ordinal).ToList();

        var worn = new List<EquippedPiece>();
        var expected = new[] { 0, 0, 1, 2, 3 };  // container count after 0..4 pieces
        for (var i = 0; i <= order.Count; i++)
        {
            var grant = ThresholdEvaluator.Grant(consumer, SetEvaluator.Hits(worn, new[] { set }));
            Assert.Equal(i, grant.Count);
            Assert.Equal(expected[i], grant.WantedContainerIds.Count);
            if (i < order.Count) worn.Add(new EquippedPiece(order[i].Role, order[i].ContainerId));
        }

        Assert.Equal(new[]
        {
            "set.frostbitten-vanguard-001-02",
            "set.frostbitten-vanguard-001-03",
            "set.frostbitten-vanguard-001-04",
        }, ThresholdEvaluator.Grant(consumer, SetEvaluator.Hits(worn, new[] { set })).WantedContainerIds);
    }

    /// <summary>Which sets each authored <c>(role, base type)</c> member pair belongs to.</summary>
    static Dictionary<(ItemRole Role, string ContainerId), List<string>> MemberOwners(IReadOnlyList<SetDef> sets)
    {
        var owners = new Dictionary<(ItemRole, string), List<string>>();
        foreach (var set in sets)
            foreach (var m in set.Members)
            {
                var key = (m.Role, m.ContainerId);
                if (!owners.TryGetValue(key, out var list)) owners[key] = list = new List<string>();
                if (!list.Contains(set.SetId)) list.Add(set.SetId);
            }
        return owners;
    }

    [Fact]
    public void One_shipped_item_can_advance_more_than_one_set_and_the_corpus_already_relies_on_it()
    {
        // A single equipped item can legitimately advance several set counts. The evaluator counts
        // per set id, never as one merged count.
        var sets = Sets();
        var owners = MemberOwners(sets);

        Assert.NotEmpty(owners);
        var shared = owners.First(kv => kv.Value.Count > 1);

        var progress = SetEvaluator.Progress(new[] { new EquippedPiece(shared.Key.Role, shared.Key.ContainerId) }, sets);
        Assert.Equal(shared.Value.Count, progress.Count);
        Assert.All(progress, p => Assert.Equal(1, p.Count));
    }

    [Fact]
    public void Two_real_shipped_sets_worn_together_stay_independent()
    {
        var sets = Sets();
        var a = sets.Single(s => s.SetId == "frostbitten-vanguard-001");
        var b = sets.Single(s => s.SetId == "sunwoven-almanac-003");

        // These sets may now share every authored piece with the generated population. Their
        // progress must nevertheless be independently counted for the pieces they name.
        IEnumerable<EquippedPiece> FirstTwo(SetDef s) => s.Members
            .Take(2)
            .Select(m => new EquippedPiece(m.Role, m.ContainerId));

        var worn = FirstTwo(a).Concat(FirstTwo(b)).ToList();
        Assert.Equal(4, worn.Count);

        var progress = SetEvaluator.Progress(worn, sets);
        var pair = progress.Where(p => p.SetId == a.SetId || p.SetId == b.SetId).ToList();
        Assert.Equal(2, pair.Count);
        Assert.All(pair, p => Assert.Equal(2, p.Count));
        Assert.All(pair, p => Assert.Single(p.WantedContainerIds));

        // And their sources never collide, which is what makes withdrawing one safe.
        Assert.NotEqual(SetEvaluator.Consumer(a).SourceKey, SetEvaluator.Consumer(b).SourceKey);
    }

    [Fact]
    public void No_shipped_set_id_can_collide_with_one_of_its_own_tier_ids()
    {
        // The corpus uses a THREE-digit sequence (`-001`), the tier pad is two digits, so the grammar's
        // "a set_id may not end in -NN" rule is satisfied by every shipped row rather than by luck.
        foreach (var set in Sets())
            Assert.DoesNotMatch(@"-[0-9]{2}$", set.SetId);
    }

    // ---- the shipped charm catalog -----------------------------------------------------------------

    [Fact]
    public void The_shipped_charm_population_adheres_to_the_class_contract()
    {
        var charms = Charms();
        Assert.NotEmpty(charms);
        Assert.Contains(charms, c => c.Class == CharmClass.Minor);
        Assert.Contains(charms, c => c.Class == CharmClass.Standard);
        Assert.Contains(charms, c => c.Class == CharmClass.Signet);

        Assert.All(charms, c => Assert.Contains(c.ApCost, new[] { 1, 2, 3, 5 }));
    }

    [Fact]
    public void A_signet_has_no_rolled_half_and_enhance_refuses_rather_than_no_ops()
    {
        var charms = Charms();
        foreach (var signet in charms.Where(c => c.Class == CharmClass.Signet))
        {
            Assert.False(signet.HasRolledHalf);
            Assert.Equal(0, signet.PrefixRolls);
            Assert.Equal(0, signet.SuffixRolls);
        }

        // And the rule is enforced, not merely observed: a signet WITH a rolled half is refused, so
        // module 15 can key its refusal on the class rather than on a roll outcome.
        var ex = Assert.Throws<CharmCorpusRejection>(() => CharmCorpus.ValidateClassRules(
            new CharmDef("charm.bad", "Bad", "offense", CharmClass.Signet, 5, true, 1, 0, true)));
        Assert.Contains("threshold.charm-signet-has-rolled-half", ex.Rejection.Detail);
    }

    [Fact]
    public void A_signet_caps_at_one_copy_while_other_classes_cap_at_two()
    {
        var charms = Charms();
        Assert.All(charms.Where(c => c.Class == CharmClass.Signet), c => Assert.True(c.UniqueCarry));
        Assert.All(charms.Where(c => c.Class != CharmClass.Signet), c => Assert.False(c.UniqueCarry));

        Assert.Contains("threshold.charm-signet-not-unique-carry",
            Assert.Throws<CharmCorpusRejection>(() => CharmCorpus.ValidateClassRules(
                new CharmDef("charm.bad", "Bad", "offense", CharmClass.Signet, 5, false, 0, 0, true))).Rejection.Detail);
    }

    [Fact]
    public void A_signets_drawback_atom_binds_with_the_container_and_cannot_be_dropped()
    {
        // §6.1: every shipped signet carries an authored NEGATIVE atom inside its fixed core, so it
        // binds with the rest of the container and never as a separable row. No other class does.
        var charms = Charms();
        Assert.NotEmpty(charms.Where(c => c.Class == CharmClass.Signet));
        Assert.All(charms.Where(c => c.Class == CharmClass.Signet), c => Assert.True(c.HasNegativeAtom));
        Assert.DoesNotContain(charms, c => c.Class != CharmClass.Signet && c.HasNegativeAtom);

        Assert.Contains("threshold.charm-signet-has-no-drawback",
            Assert.Throws<CharmCorpusRejection>(() => CharmCorpus.ValidateClassRules(
                new CharmDef("charm.bad", "Bad", "offense", CharmClass.Signet, 5, true, 0, 0, false))).Rejection.Detail);
    }

    [Fact]
    public void Charm_class_is_authored_and_never_derived_from_ap_cost()
    {
        // The two are perfectly correlated today — and that is exactly why the class must stay a
        // column: a future 2-AP signet has to remain representable.
        var charms = Charms();
        Assert.All(charms.Where(c => c.ApCost == 1), c => Assert.Equal(CharmClass.Minor, c.Class));
        Assert.All(charms.Where(c => c.ApCost is 2 or 3), c => Assert.Equal(CharmClass.Standard, c.Class));
        Assert.All(charms.Where(c => c.ApCost == 5), c => Assert.Equal(CharmClass.Signet, c.Class));

        const string twoApSignet = """
            { "entries": [ {
              "id": "charm.future-001", "name": "A 2-AP signet", "axis": "offense",
              "charmClass": "signet", "apCost": 2, "uniqueCarry": true,
              "prefixRolls": 0, "suffixRolls": 0,
              "fixedAtoms": [ { "family": "atom.might", "powerBand": "high" },
                              { "family": "atom.vitality", "powerBand": "low", "params": { "sign": "negative" } } ]
            } ] }
            """;
        var parsed = CharmCorpus.Parse(twoApSignet).Single();
        Assert.Equal(CharmClass.Signet, parsed.Class);
        Assert.Equal(2, parsed.ApCost);
    }

    // ---- the shipped resonance table ---------------------------------------------------------------

    [Fact]
    public void The_resonance_table_is_five_axes_at_two_and_three_charms()
    {
        var rows = Resonances();
        var axes = CharmResonance.AxesOf(rows);
        Assert.Equal(new[] { "control", "economy", "offense", "survivability", "utility" }, axes);
        Assert.All(rows, r => Assert.Contains(r.CountRequired, new[] { 2, 3 }));
        // Formula, not a literal (population-pin SE3.3, 2026-09-19): every axis has exactly one
        // 2-count and one 3-count row, no more, no fewer -- proven per axis, not just as a raw total.
        foreach (var axis in axes)
            Assert.Equal(
                new[] { 2, 3 },
                rows.Where(r => r.Axis == axis).Select(r => r.CountRequired).OrderBy(c => c).ToArray());
        Assert.Equal(axes.Count * 2, rows.Count);
    }

    [Fact]
    public void A_real_axis_resonance_grants_cumulatively_from_the_real_table()
    {
        var rows = Resonances();
        var consumer = CharmResonance.Consumer("offense", rows);
        var charms = Charms().Where(c => c.Axis == "offense").Take(3)
            .Select(c => new HeldCharm(c.ContainerId, c.Axis)).ToList();

        Assert.Equal(1, ThresholdEvaluator.Grant(consumer, charms.Take(1)).Count);
        Assert.Empty(ThresholdEvaluator.Grant(consumer, charms.Take(1)).WantedContainerIds);
        Assert.Equal(new[] { "charm.res-offense-02" },
            ThresholdEvaluator.Grant(consumer, charms.Take(2)).WantedContainerIds);
        Assert.Equal(new[] { "charm.res-offense-02", "charm.res-offense-03" },
            ThresholdEvaluator.Grant(consumer, charms).WantedContainerIds);
    }

    [Fact]
    public void Two_axes_never_merge_into_one_count()
    {
        var rows = Resonances();
        var mixed = new[]
        {
            new HeldCharm("charm.a", "offense"),
            new HeldCharm("charm.b", "control"),
            new HeldCharm("charm.c", "control"),
        };

        Assert.Empty(ThresholdEvaluator.Grant(CharmResonance.Consumer("offense", rows), mixed).WantedContainerIds);
        Assert.Equal(new[] { "charm.res-control-02" },
            ThresholdEvaluator.Grant(CharmResonance.Consumer("control", rows), mixed).WantedContainerIds);
    }

    [Fact]
    public void All_ten_shipped_resonance_ids_are_unpadded_and_the_divergence_is_measured_not_normalised_away()
    {
        // ⛔ The shipped corpus writes `charm.res-offense-2`; the grammar this module enforces (and the
        // ordinal sort in RpgStore.ListBindings) wants `charm.res-offense-02`. Ten rows, and it is a
        // rename rather than a migration — but it is NOT this module's to perform: the ids are
        // seedsmith-allocated (`NamespaceAllocation.cs` reads them out of `idNamespaces.charms
        // .resonanceNote`) and the corpus belongs to module 13. Measured here so the rename has a
        // number attached and cannot be forgotten.
        var rows = Resonances();
        Assert.Equal(10, rows.Count(r => r.IsAuthoredUnpadded));
        Assert.All(rows, r => Assert.Matches(@"^charm\.res-[a-z]+-[0-9]$", r.AuthoredContainerId));
        Assert.All(rows, r => Assert.Matches(@"^charm\.res-[a-z]+-[0-9]{2}$", r.ContainerId));

        // Padded, the ten sort into numeric order; unpadded they would too at counts 2-3, which is
        // exactly why nobody has noticed. The defect bites at count 10.
        Assert.Equal("charm.res-offense-02", ThresholdContainerIds.CharmResonance("offense", 2));
    }

    // ---- the role registry the predicate reads -----------------------------------------------------

    [Fact]
    public void The_three_previously_disagreeing_hybrid_role_sources_now_agree()
    {
        // spec-threshold-grants.md calls this "a blocking contradiction in the shipped role vocabulary"
        // and names three 13-role/895 permille sources against D3's twelve. VERIFIED FACTS WIN: module 3
        // already landed D30 as a registryVersion 2 bump in the frozen file, and both Python constants
        // moved with it. Pinned here so a regression is a named failure rather than a silent one.
        var registryJson = File.ReadAllText(Path.Combine(ItemsDir(), "_registry", "core.v1.json"));
        Assert.Contains("\"registryVersion\": 2", registryJson);

        var registry = ItemRoleRegistry.Parse(registryJson);
        var dropped = registry.Where(r => !r.HybridEligible && r.Role != ItemRole.Standard)
            .Select(r => ItemRoles.Id(r.Role)).OrderBy(i => i, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "head-guard", "sense", "ward-array" }, dropped);

        foreach (var (path, constant) in new[]
                 {
                     (Path.Combine("tools", "seedsmith", "seedsmith", "adapters", "items", "registries.py"), "HYBRID_FRAME_EXCLUDED_ROLES"),
                     (Path.Combine("tools", "seedsmith", "seedsmith", "metrics", "linkage.py"), "NON_HYBRID_ROLES"),
                 })
        {
            var text = File.ReadAllText(Path.Combine(ThresholdGrantTests.RepoRoot(), path));
            var line = text.Split('\n').Single(l => l.Contains(constant + " = frozenset", StringComparison.Ordinal));
            Assert.Contains("ward-array", line);
            Assert.Contains("head-guard", line);
            Assert.Contains("sense", line);
            Assert.DoesNotContain("jewel-minor-b", line);
        }
    }

    [Fact]
    public void The_six_cheapest_hybrid_core_roles_are_the_ones_the_defect_names()
    {
        var core = ThresholdGrantTests.HybridCore();
        var cheapest = core.OrderBy(kv => kv.Value).ThenBy(kv => ItemRoles.Id(kv.Key), StringComparer.Ordinal)
            .Take(6).ToList();

        Assert.Equal(new[] { "jewel-minor-a", "jewel-minor-b", "retinue", "footing", "infusion", "girdle" },
            cheapest.Select(kv => ItemRoles.Id(kv.Key)).OrderBy(i => i, StringComparer.Ordinal)
                .OrderBy(i => core[ItemRoles.TryParse(i, out var r) ? r : ItemRole.Standard])
                .ThenBy(i => i, StringComparer.Ordinal));
        Assert.Equal(230, cheapest.Sum(kv => kv.Value));
    }
}
