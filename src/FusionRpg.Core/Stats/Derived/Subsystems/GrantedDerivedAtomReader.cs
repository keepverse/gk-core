using System.Globalization;
using System.Text.Json;
using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Stats.Derived.Subsystems;

/// <summary>
/// The Unity-free half of the lawn `stat.derived` executor: turns the grants an
/// <see cref="IEffectGrantStore"/> already holds for an actor into the <see cref="BoundDerivedAtom"/>
/// list <see cref="AtomDerivedSubsystem"/> composes.
///
/// <para><b>Why this lives in Core (aura-skill-todo.md Phase 5 / TC3).</b> It used to live entirely in
/// <c>FusionRpg.Injector.Stats.GrantedDerivedAtoms</c>, which cannot be reached by any test that CI
/// runs: the injector targets net6.0 and references the game's BepInEx/Il2Cpp interop assemblies, so
/// building it needs a real PVZ Fusion install — and `ci.yml`'s test step names ten projects, none of
/// them the injector. A guard nobody can run is not a guard. TC3's own rule was *"if the decision
/// cannot be reached without a Unity host, extract the decision into a Unity-free type, leaving only
/// the field pokes in the untestable shell"* — this is that extraction.</para>
///
/// <para>Everything here is Unity-free by construction: <see cref="IEffectGrantStore"/>,
/// <see cref="EffectGrant"/> and <see cref="EffectOwnerKeys"/> are all <c>FusionRpg.Contracts</c>
/// types, and <see cref="StatContext"/> is Core's. What stays in the injector is exactly the part that
/// is genuinely host-specific: reaching the live <c>EffectRuntime.Bag</c> static.</para>
///
/// <para><b>Scope grammar is the shipped one, not a new one:</b> `match`, `plant:{typeId}` /
/// `zombie:{typeId}`, and `entity:{ptr}`. `instance:{guid}` deliberately never appears —
/// <c>UniqueOwnerBinder</c> rewrites it to `entity:{ptr}` at Bound, and unique-entity-effects.md
/// forbids it reaching a hot resolve.</para>
/// </summary>
public static class GrantedDerivedAtomReader
{
    /// <summary>
    /// Overlay keys, deliberately NAMESPACED.
    ///
    /// <para>An earlier draft of this reader matched bare <c>channel</c>/<c>op</c>/<c>amount</c>, which
    /// are exactly the keys <c>InjectorEffectActionSink</c> already reads for <b>FA1 ModifyStat</b>
    /// (line 80) and <b>FA10 ApplyResourceDelta</b> (line 132). Every FA1 grant on the board would
    /// therefore have been consumed a second time as a derived mod — applied once as a primary stat
    /// modifier and again as a derived channel. Caught before shipping, by asking what else writes
    /// these keys rather than assuming nothing did.</para>
    ///
    /// <para>The namespace makes the collision impossible by construction rather than by convention:
    /// only a <c>stat.derived</c> compilation emits these, so an FA1/FA10 overlay can never be mistaken
    /// for one no matter how its own keys evolve. <c>GrantedDerivedAtomReaderTests</c> is the
    /// regression test that claim previously lacked — before TC3 it was asserted by a comment alone.</para>
    /// </summary>
    /// <summary>The four derived ops, as they appear as KEYS on a compiled action row
    /// (<c>AtomCompiler.ToOpcodeShape</c>). Order is the search order; a row carries exactly one.
    /// Mirrors <see cref="AtomDerivedSubsystem.TryParseOp"/>'s accepted set — there is deliberately no
    /// <c>more</c> on the derived side.</summary>
    static readonly string[] DerivedOpKeys = { "flat", "increased", "replace", "flag" };

    public const string ChannelKey = "derived.channel";

    /// <inheritdoc cref="ChannelKey"/>
    public const string OpKey = "derived.op";

    /// <inheritdoc cref="ChannelKey"/>
    public const string AmountKey = "derived.amount";

    /// <summary>
    /// Every bound derived atom that applies to this actor, from every owner scope it belongs to.
    /// Returns an empty array — never null — when nothing is granted, so
    /// <see cref="AtomDerivedSubsystem"/>'s own "contribute nothing rather than a zero-valued modifier"
    /// rule holds by construction.
    /// </summary>
    public static IReadOnlyList<BoundDerivedAtom> Read(IEffectGrantStore? grants, StatContext? ctx) =>
        Read(grants, catalog: null, ctx);

    /// <summary>
    /// The full read, including the <b>production</b> transport.
    ///
    /// <para><b>Two transports, and the catalog one is the real path (TC2, 2026-08-30).</b>
    /// <c>BattlefieldOwnSideReactor.BuildGrant</c> — the only production grant path — emits an
    /// <c>EffectId</c> and <b>no overlay whatsoever</b>. The values live on the compiled def's
    /// <c>ModifyDerivedStat</c> action rows, whose params are <c>channel</c>/<c>op</c>/<c>amount</c>
    /// exactly as the <c>stat.derived</c> ParamSchema declares them. Passing <paramref name="catalog"/>
    /// is what makes a real aura reach a lawn entity; without it this reader sees only the
    /// direct-grant/debug shape below and a real grant yields nothing.</para>
    ///
    /// <para><b>Being catalog-aware also removes the FA1 collision structurally.</b> The namespaced
    /// overlay keys existed only because the old reader scanned every grant's overlay blindly and could
    /// not tell an FA1 <c>ModifyStat</c> grant from a derived one. Matching on the def's action id
    /// cannot make that mistake: an FA1 def has no <c>ModifyDerivedStat</c> row. So the catalog path
    /// reads the bare, schema-declared names safely, while the overlay path keeps its namespace for the
    /// catalog-less case.</para>
    /// </summary>
    public static IReadOnlyList<BoundDerivedAtom> Read(IEffectGrantStore? grants, IEffectCatalog? catalog, StatContext? ctx)
    {
        if (grants is null || ctx is null) return Array.Empty<BoundDerivedAtom>();

        List<BoundDerivedAtom>? found = null;

        // ⚠️ Owner KEYS must be the full, prefixed ids from EffectOwnerKeys — never the bare typeId or
        // ptr. `IEffectGrantStore.ForOwner` compares `StatApplyScope.Normalize(ownerKey)` on both
        // sides, and that normaliser is NOT prefix-agnostic: it maps `entity:0xAB` -> `entity:ab` but
        // leaves a bare `0xAB` as `0xab`, so the two can never be equal. An earlier version of this
        // reader passed `ctx.TypeId.ToString()` and `ctx.EntityKey` raw, which meant **two of the three
        // scopes silently matched nothing** against a real production grant — `match` worked (its key
        // is already the literal "match") and hid the bug. Caught by a test built to mimic
        // `BattlefieldOwnSideReactor.BuildGrant`'s actual output rather than the reader's own habits.
        Collect(grants, catalog, "match", EffectOwnerKeys.Match, ref found);

        var sideKind = ctx.Side == StatSide.Plant ? "plant" : "zombie";
        var typeKey = ctx.Side == StatSide.Plant
            ? EffectOwnerKeys.PlantType(ctx.TypeId)
            : EffectOwnerKeys.ZombieType(ctx.TypeId);
        Collect(grants, catalog, sideKind, typeKey, ref found);

        if (!string.IsNullOrWhiteSpace(ctx.EntityKey))
            Collect(grants, catalog, "entity", EffectOwnerKeys.Entity(ctx.EntityKey!), ref found);

        return (IReadOnlyList<BoundDerivedAtom>?)found ?? Array.Empty<BoundDerivedAtom>();
    }

    static void Collect(IEffectGrantStore grants, IEffectCatalog? catalog, string ownerKind, string ownerKey,
        ref List<BoundDerivedAtom>? into)
    {
        IReadOnlyList<EffectGrant> list;
        try { list = grants.ForOwner(ownerKind, ownerKey); }
        catch { return; }

        if (list is null) return;

        for (var i = 0; i < list.Count; i++)
        {
            var g = list[i];
            if (g is null) continue;

            // ── the PRODUCTION transport: the def's own ModifyDerivedStat action rows ──────────────
            // Matching on the action id is what makes this collision-proof: an FA1 ModifyStat def has
            // no ModifyDerivedStat row, so its bare `channel` can never be mistaken for a derived one.
            if (catalog is not null && CollectFromDef(catalog, g, ref into)) continue;

            if (g.Overlay is null || g.Overlay.Count == 0) continue;

            if (!TryString(g.Overlay, ChannelKey, out var channel)) continue;
            if (!TryString(g.Overlay, OpKey, out var op)) continue;
            // An op the derived side does not have (there is no `More` here) is content that would
            // otherwise be silently coerced into a wrong-but-plausible number. Skip it; the bind gate
            // is where such a row is meant to be refused, and coercing here would hide that it wasn't.
            if (!AtomDerivedSubsystem.TryParseOp(op, out var parsed)) continue;
            if (!TryDouble(g.Overlay, AmountKey, out var amount)) continue;

            (into ??= new List<BoundDerivedAtom>()).Add(
                new BoundDerivedAtom(channel, parsed, amount,
                    SourceId: ContributionSourceIds.Grant(
                        string.IsNullOrWhiteSpace(g.EffectId) ? g.GrantId : g.EffectId)));
        }
    }

    /// <summary>
    /// Reads every <c>ModifyDerivedStat</c> action row on this grant's def. Returns true when the def
    /// was found and is a derived-stat one, so the caller skips the overlay fallback for it.
    ///
    /// <para>The grant's overlay, when present, <b>wins</b> over the def's authored params — that is
    /// the whole point of an overlay, and it matches how <c>EffectOverlayMerge</c> treats every other
    /// action. Overlay keys here are the bare, schema-declared names (<c>channel</c>/<c>op</c>/
    /// <c>amount</c>), which is safe precisely because we already know this def is derived-stat.</para>
    /// </summary>
    static bool CollectFromDef(IEffectCatalog catalog, EffectGrant g, ref List<BoundDerivedAtom>? into)
    {
        EffectDef? def;
        try { def = catalog.Get(g.EffectId); }
        catch { return false; }

        if (def is null || def.Actions is null || def.Actions.Count == 0) return false;

        var sawDerivedRow = false;

        for (var i = 0; i < def.Actions.Count; i++)
        {
            var row = def.Actions[i];
            if (row?.Params is null) continue;
            if (!string.Equals(row.Action, EffectActions.ModifyDerivedStat, StringComparison.OrdinalIgnoreCase))
                continue;

            sawDerivedRow = true;

            // Channel: authored on the row, overridable by the grant overlay (overlay wins, exactly
            // as EffectOverlayMerge treats every other action).
            if (!TryString(row.Params, "channel", out var channel)
                && !(g.Overlay is not null && TryString(g.Overlay, "channel", out channel))) continue;
            if (g.Overlay is not null && TryString(g.Overlay, "channel", out var chOverride)) channel = chOverride;

            // Op-as-KEY, not an `op` param: AtomCompiler.ToOpcodeShape rewrites the authored
            // {op:"flat", amount:150} into {flat:150} before it ever reaches a def -- the same shape
            // stat.modify compiles to. Whichever of the four derived ops is present IS the op, and its
            // value is the amount. Reading `op`/`amount` here would work only for hand-built defs and
            // would silently miss every compiled one.
            var matched = false;
            foreach (var opName in DerivedOpKeys)
            {
                double amount;
                if (g.Overlay is not null && TryDouble(g.Overlay, opName, out amount)) { }
                else if (!TryDouble(row.Params, opName, out amount)) continue;

                if (!AtomDerivedSubsystem.TryParseOp(opName, out var parsed)) continue;

                (into ??= new List<BoundDerivedAtom>()).Add(
                    new BoundDerivedAtom(channel, parsed, amount,
                        SourceId: ContributionSourceIds.Grant(
                            string.IsNullOrWhiteSpace(g.EffectId) ? g.GrantId : g.EffectId)));
                matched = true;
                break;
            }

            if (!matched) continue;
        }

        return sawDerivedRow;
    }

    static bool TryString(IReadOnlyDictionary<string, object?> overlay, string key, out string value)
    {
        value = "";
        if (!overlay.TryGetValue(key, out var raw) || raw is null) return false;
        if (raw is string s) { value = s; return !string.IsNullOrWhiteSpace(s); }
        if (raw is JsonElement je && je.ValueKind == JsonValueKind.String)
        {
            value = je.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(value);
        }
        return false;
    }

    static bool TryDouble(IReadOnlyDictionary<string, object?> overlay, string key, out double value)
    {
        value = 0;
        if (!overlay.TryGetValue(key, out var raw) || raw is null) return false;
        switch (raw)
        {
            case double d: value = d; return true;
            case float f: value = f; return true;
            case long l: value = l; return true;
            case int i: value = i; return true;
            case JsonElement je when je.ValueKind == JsonValueKind.Number:
                return je.TryGetDouble(out value);
            default:
                return double.TryParse(
                    Convert.ToString(raw, CultureInfo.InvariantCulture),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
