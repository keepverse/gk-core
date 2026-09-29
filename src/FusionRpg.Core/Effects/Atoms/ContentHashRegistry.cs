namespace FusionRpg.Core.Effects.Atoms;

/// <summary>One hashed column. <paramref name="IsJson"/> selects canonical JSON over raw text.</summary>
public readonly record struct ContentHashColumn(string Name, bool IsJson)
{
    public static ContentHashColumn Text(string name) => new(name, false);
    public static ContentHashColumn Json(string name) => new(name, true);
}

/// <summary>
/// One covered table and the columns hashed from it, in declared order.
///
/// <para>The column list is <b>explicit</b> rather than read from <c>PRAGMA table_info</c>. Reading it
/// from the database would make adding a column move every stamp silently — the exact accident the
/// versioned registry exists to prevent. Adding a column is a registry edit and a version bump.</para>
/// </summary>
public sealed record ContentHashTable(string TableName, IReadOnlyList<ContentHashColumn> Columns);

/// <summary>
/// Which tables the content hash covers, versioned (spec-content-hash.md, definitions §8).
///
/// <para><b>A registry, not a fixed list.</b> E9 adds <c>power_coefficient</c> and
/// <c>power_trigger_frequency</c>; E18 adds the element roster and both matrices — all after this
/// module ships. A fixed list would silently invalidate every stamp E11 made, and the refuse-on-
/// mismatch rule would turn that into a hard failure of the whole Checkpoint D corpus. An added table
/// is an explicit, attributable version bump instead.</para>
///
/// <para><b>Order is the table name, ordinal.</b> Not module registration order: reordering code
/// initialisation would then move the hash with no content change at all.</para>
/// </summary>
public static class ContentHashRegistry
{
    /// <summary>Bump when a table joins or leaves, or a covered table's column list changes.
    /// E18 → 2, E9 → 3, E16 → 4, cap-consolidation (T1) → 5, action program T30 → 6,
    /// action program P0.3 → 7, effect-pipeline T3.1 (affix-schema) → 8,
    /// effect-pipeline T3.2 (prefix/suffix split) → 9 — all done.</summary>
    public const int CurrentSchemaVersion = 9;

    /// <summary>
    /// Version 1: the tables E2, E4 and E5 actually created. Instances, bindings and
    /// <c>content_meta</c> are absent on purpose — content is hashed, player state is not, and
    /// <c>content_meta</c> holds the revision rather than any content.
    /// </summary>
    static readonly ContentHashTable[] V1 = Sorted(new[]
    {
        new ContentHashTable("effect_atom", new[]
        {
            ContentHashColumn.Text("atom_id"),
            ContentHashColumn.Text("kind_id"),
            ContentHashColumn.Text("family_id"),
            ContentHashColumn.Text("variant"),
            ContentHashColumn.Text("tier"),
            ContentHashColumn.Text("name"),
            ContentHashColumn.Json("when_json"),
            ContentHashColumn.Json("params_json"),
            ContentHashColumn.Json("tags_json"),
            ContentHashColumn.Json("power_json"),
            ContentHashColumn.Json("power_override_json"),
            ContentHashColumn.Text("power_note"),
            ContentHashColumn.Text("icd_key"),
            ContentHashColumn.Text("trigger_id"),
            ContentHashColumn.Text("enabled"),
            ContentHashColumn.Text("revision"),
        }),
        new ContentHashTable("effect_container", new[]
        {
            ContentHashColumn.Text("container_id"),
            ContentHashColumn.Text("container_kind"),
            ContentHashColumn.Text("slot"),
            ContentHashColumn.Text("rarity"),
            ContentHashColumn.Text("min_tier"),
            ContentHashColumn.Text("max_tier"),
            ContentHashColumn.Text("level_req"),
            ContentHashColumn.Text("pool_rolls"),
            ContentHashColumn.Json("tags_json"),
            ContentHashColumn.Text("enabled"),
            ContentHashColumn.Text("revision"),
        }),
        new ContentHashTable("effect_container_atom", new[]
        {
            ContentHashColumn.Text("container_id"),
            ContentHashColumn.Text("seq"),
            ContentHashColumn.Text("atom_id"),
            ContentHashColumn.Json("overrides_json"),
        }),
        new ContentHashTable("effect_container_pool", new[]
        {
            ContentHashColumn.Text("container_id"),
            ContentHashColumn.Text("atom_id"),
            ContentHashColumn.Text("weight"),
            ContentHashColumn.Text("group_key"),
        }),
        new ContentHashTable("effect_curve", new[]
        {
            ContentHashColumn.Text("curve_id"),
            ContentHashColumn.Text("input"),
            ContentHashColumn.Json("points_json"),
            ContentHashColumn.Text("revision"),
        }),
        new ContentHashTable("rarity", new[]
        {
            ContentHashColumn.Text("rarity_id"),
            ContentHashColumn.Text("ordinal"),
            ContentHashColumn.Text("pool_rolls"),
            ContentHashColumn.Text("min_tier"),
            ContentHashColumn.Text("max_tier"),
        }),
    });

    /// <summary>The covered set for a version, already in hash order.</summary>
    /// <summary>
    /// Version 2 adds E18's three tables: the element roster and both matchup matrices. An element
    /// addition now changes the content hash <b>and</b> the generated channel count together, so it
    /// can never be mistaken for a code regression and a golden that moves has an attributable cause.
    /// </summary>
    static readonly ContentHashTable[] V2 = Sorted(V1.Concat(new[]
    {
        new ContentHashTable("effect_element", new[]
        {
            ContentHashColumn.Text("element_id"),
            ContentHashColumn.Text("display_name"),
            ContentHashColumn.Text("ordinal"),
            ContentHashColumn.Text("enabled"),
            ContentHashColumn.Text("revision"),
        }),
        new ContentHashTable("effect_element_matrix_combat", new[]
        {
            ContentHashColumn.Text("attacker_element"),
            ContentHashColumn.Text("defender_element"),
            ContentHashColumn.Text("unit"),
        }),
        new ContentHashTable("effect_element_matrix_shield", new[]
        {
            ContentHashColumn.Text("attacker_element"),
            ContentHashColumn.Text("defender_element"),
            ContentHashColumn.Text("unit"),
        }),
    }).ToArray());

    /// <summary>
    /// Version 3 adds E9's two authored price tables.
    ///
    /// <para><c>power_coefficient_proposal</c> is deliberately <b>absent</b>. A sweep writes
    /// proposals and never touches what ships; if a proposal moved the stamp, running the sweep would
    /// make every replay verdict downstream report a content mismatch for a number nobody adopted.</para>
    /// </summary>
    static readonly ContentHashTable[] V3 = Sorted(V2.Concat(new[]
    {
        new ContentHashTable("power_coefficient", new[]
        {
            ContentHashColumn.Text("kind_id"),
            ContentHashColumn.Text("channel"),
            ContentHashColumn.Text("coeff_milli"),
            ContentHashColumn.Text("reference_scale"),
        }),
        new ContentHashTable("power_trigger_frequency", new[]
        {
            ContentHashColumn.Text("trigger_id"),
            ContentHashColumn.Text("per_minute"),
        }),
    }).ToArray());

    /// <summary>
    /// Version 4 adds E16's <c>effect_channel_policy</c>.
    ///
    /// <para>The table and this bump ship together on purpose. The 0.95 resist cap was a code
    /// constant, where editing it moved every battle golden with the stamp standing still —
    /// acceptable only because a constant edit is visible in a diff, which stops being true the
    /// moment it becomes a row.</para>
    /// </summary>
    static readonly ContentHashTable[] V4 = Sorted(V3.Concat(new[]
    {
        new ContentHashTable("effect_channel_policy", new[]
        {
            ContentHashColumn.Text("channel_id"),
            ContentHashColumn.Text("direction"),
            ContentHashColumn.Text("default_value"),
            ContentHashColumn.Text("cap_milli"),
            ContentHashColumn.Text("compose_kind"),
        }),
    }).ToArray());

    /// <summary>
    /// Version 5 narrows <c>effect_channel_policy</c> to <c>channel_id</c>/<c>direction</c>
    /// (cap-consolidation, T1, 2026-08-24) — <c>default_value</c>, <c>cap_milli</c> and
    /// <c>compose_kind</c> retired as dead columns nothing ever read; a derived cap's one home is now
    /// <c>gk-core/data/tuning/derived-stats.v1.json</c>.
    ///
    /// <para><b>This is a table-shape change, not a gameplay change.</b> No composed number moves —
    /// the cap value itself is unchanged (0.95), only where it is enforced. The stamp moves because the
    /// hashed <i>shape</i> changed, exactly the same distinction V4's own doc comment draws for the
    /// opposite direction (a column joining). Asserted separately from golden stability in
    /// <c>ChannelPolicyStoreTests</c> and <c>DerivedStatRegistryTests</c> so a session seeing "hash
    /// changed, goldens clean" does not assume one of them is wrong.</para>
    /// </summary>
    static readonly ContentHashTable[] V5 = Sorted(
        V4.Where(t => t.TableName != "effect_channel_policy")
          .Append(new ContentHashTable("effect_channel_policy", new[]
          {
              ContentHashColumn.Text("channel_id"),
              ContentHashColumn.Text("direction"),
          }))
          .ToArray());

    /// <summary>
    /// Version 6 (T30, action program, spec-action-catalog.md R2): actions are content by the same
    /// definition as an atom — authored rows whose values change battle outcomes — so
    /// <c>rpg_action</c>, <c>rpg_action_cost</c> and <c>rpg_action_effect_scope</c> join the hash.
    /// <c>rpg_action_grant</c> stays excluded (per-player state, like <c>effect_binding</c>);
    /// <c>rpg_action_species_basics</c> is not named by the spec's own R2 table and stays out until a
    /// future revision explicitly adds it.
    /// </summary>
    static readonly ContentHashTable[] V6 = Sorted(V5.Concat(new[]
    {
        new ContentHashTable("rpg_action", new[]
        {
            ContentHashColumn.Text("action_id"),
            ContentHashColumn.Text("name"),
            ContentHashColumn.Text("kind"),
            ContentHashColumn.Text("rung"),
            ContentHashColumn.Json("tags_json"),
            ContentHashColumn.Text("enabled"),
            ContentHashColumn.Text("revision"),
            ContentHashColumn.Text("grantable"),
            ContentHashColumn.Text("default_attack_eligible"),
            ContentHashColumn.Text("container_id"),
            ContentHashColumn.Text("time_cost_ticks"),
            ContentHashColumn.Text("speed_channel"),
            ContentHashColumn.Text("cooldown_channel"),
            ContentHashColumn.Text("windup_ticks"),
            ContentHashColumn.Json("resolve_offsets_json"),
            ContentHashColumn.Text("recovery_ticks"),
            ContentHashColumn.Text("commitment"),
            ContentHashColumn.Text("interruptible"),
            ContentHashColumn.Text("interrupt_refund_milli"),
            ContentHashColumn.Text("slot_consuming"),
            ContentHashColumn.Text("priority_band"),
            ContentHashColumn.Text("cooldown_class"),
            ContentHashColumn.Text("cooldown_key"),
            ContentHashColumn.Text("cooldown_ticks"),
            ContentHashColumn.Text("starts_at"),
            ContentHashColumn.Text("interrupt_cooldown_milli"),
            ContentHashColumn.Json("target_spec_json"),
            ContentHashColumn.Text("min_range"),
            ContentHashColumn.Text("max_range"),
            ContentHashColumn.Text("range_channel"),
            ContentHashColumn.Text("requires_line_of_sight"),
            ContentHashColumn.Json("conditions_json"),
        }),
        new ContentHashTable("rpg_action_cost", new[]
        {
            ContentHashColumn.Text("action_id"),
            ContentHashColumn.Text("resource_id"),
            ContentHashColumn.Json("amount_spec_json"),
            ContentHashColumn.Text("when_paid"),
        }),
        new ContentHashTable("rpg_action_effect_scope", new[]
        {
            ContentHashColumn.Text("action_id"),
            ContentHashColumn.Text("atom_id"),
            ContentHashColumn.Text("scope"),
        }),
    }).ToArray());

    /// <summary>
    /// Version 7 (P0.3, action program, spec-power-vector.md "predicates ARE priced"): adds
    /// <c>power_predicate_frequency</c>, the four-factor conditionality chain per predicate leaf —
    /// same shape and same reasoning as V3's <c>power_trigger_frequency</c> join.
    /// </summary>
    static readonly ContentHashTable[] V7 = Sorted(V6.Concat(new[]
    {
        new ContentHashTable("power_predicate_frequency", new[]
        {
            ContentHashColumn.Text("leaf_id"),
            ContentHashColumn.Text("arg_key"),
            ContentHashColumn.Text("reachability_milli"),
            ContentHashColumn.Text("susceptibility_milli"),
            ContentHashColumn.Text("coincidence_milli"),
            ContentHashColumn.Text("uptime_milli"),
        }),
    }).ToArray());

    /// <summary>
    /// Version 8 (effect-pipeline T3.1, `affix-schema`): <c>effect_container_pool</c> now references
    /// an <b>affix</b>, not a bare atom — its <c>atom_id</c> column is <c>affix_id</c>. A column
    /// <b>rename</b> is a shape change under the same "no silent move" rule V5's own doc comment
    /// states for a rename in the other direction (columns retired, not renamed) — the covered-column
    /// list is explicit precisely so this doesn't move every stamp silently. Also adds the two new
    /// affix tables: a retuned or re-bundled affix is a content change and must move the hash, the
    /// same as every other authored table.
    /// </summary>
    static readonly ContentHashTable[] V8 = Sorted(
        V7.Where(t => t.TableName != "effect_container_pool")
          .Append(new ContentHashTable("effect_container_pool", new[]
          {
              ContentHashColumn.Text("container_id"),
              ContentHashColumn.Text("affix_id"),
              ContentHashColumn.Text("weight"),
              ContentHashColumn.Text("group_key"),
          }))
          .Append(new ContentHashTable("effect_affix", new[]
          {
              ContentHashColumn.Text("affix_id"),
              ContentHashColumn.Text("affix_class"),
              ContentHashColumn.Text("revision"),
          }))
          .Append(new ContentHashTable("effect_affix_ref", new[]
          {
              ContentHashColumn.Text("affix_id"),
              ContentHashColumn.Text("seq"),
              ContentHashColumn.Text("atom_id"),
              ContentHashColumn.Text("slot_name"),
              ContentHashColumn.Text("slot_domain"),
              ContentHashColumn.Text("slot_pick"),
              ContentHashColumn.Text("slot_atom_pattern"),
          }))
          .ToArray());

    /// <summary>
    /// Version 9 (effect-pipeline T3.2, `spec-container-schema.md:27`, "replaces the single
    /// `pool_rolls` column"): both <c>effect_container</c> and <c>rarity</c> split their single
    /// <c>pool_rolls</c> column into <c>prefix_rolls</c>/<c>suffix_rolls</c> — a rename-plus-split,
    /// the same "no silent move" shape-change rule V8's own doc comment states for its column rename.
    /// </summary>
    static readonly ContentHashTable[] V9 = Sorted(
        V8.Where(t => t.TableName != "effect_container" && t.TableName != "rarity")
          .Append(new ContentHashTable("effect_container", new[]
          {
              ContentHashColumn.Text("container_id"),
              ContentHashColumn.Text("container_kind"),
              ContentHashColumn.Text("slot"),
              ContentHashColumn.Text("rarity"),
              ContentHashColumn.Text("min_tier"),
              ContentHashColumn.Text("max_tier"),
              ContentHashColumn.Text("level_req"),
              ContentHashColumn.Text("prefix_rolls"),
              ContentHashColumn.Text("suffix_rolls"),
              ContentHashColumn.Json("tags_json"),
              ContentHashColumn.Text("enabled"),
              ContentHashColumn.Text("revision"),
          }))
          .Append(new ContentHashTable("rarity", new[]
          {
              ContentHashColumn.Text("rarity_id"),
              ContentHashColumn.Text("ordinal"),
              ContentHashColumn.Text("prefix_rolls"),
              ContentHashColumn.Text("suffix_rolls"),
              ContentHashColumn.Text("min_tier"),
              ContentHashColumn.Text("max_tier"),
          }))
          .ToArray());

    public static IReadOnlyList<ContentHashTable> For(int schemaVersion) => schemaVersion switch
    {
        1 => V1,
        2 => V2,
        3 => V3,
        4 => V4,
        5 => V5,
        6 => V6,
        7 => V7,
        8 => V8,
        9 => V9,
        _ => throw new ArgumentOutOfRangeException(nameof(schemaVersion),
            $"contentHashSchemaVersion {schemaVersion} is not a known registry version " +
            $"(latest is {CurrentSchemaVersion})"),
    };

    public static IReadOnlyList<ContentHashTable> Current => For(CurrentSchemaVersion);

    static ContentHashTable[] Sorted(ContentHashTable[] tables) =>
        tables.OrderBy(t => t.TableName, StringComparer.Ordinal).ToArray();
}
