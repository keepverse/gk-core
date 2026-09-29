namespace FusionRpg.CheatCore;

public enum CheatFieldRole
{
    Toggle,
    /// <summary>Tab A percent — identity = 1 when unset.</summary>
    ScalePercent,
    /// <summary>Tab A flat — identity = 0 when unset.</summary>
    ScaleFlat,
    /// <summary>Tab B/C absolute — unset = absent (legacy sentinel -1).</summary>
    Absolute,
    /// <summary>Extra numeric (intervals, etc.) — unset = absent (legacy -1).</summary>
    Extra,
    /// <summary>Board / eco / bullet helpers with a display default that is not applied unless set.</summary>
    Config
}

public sealed class CheatFieldMeta
{
    public string Id { get; init; } = "";
    public CheatFieldRole Role { get; init; }
    public string Channel { get; init; } = "";
    public string Op { get; init; } = "";
    public string Kind { get; init; } = "number";
    /// <summary>Display / registry seed only — not an applied value when IsSet is false.</summary>
    public double DisplayDefault { get; init; }
    public bool ToggleDefault { get; init; }
    public string GroupPrefix { get; init; } = "";
}

/// <summary>Field metadata: identity lives here; stored/applied mods are absence-based.</summary>
public static class CheatSchema
{
    static readonly Dictionary<string, CheatFieldMeta> ById;
    public static IReadOnlyList<CheatFieldMeta> All { get; }

    static CheatSchema()
    {
        var list = new List<CheatFieldMeta>();
        void T(string id, bool def = false) =>
            list.Add(new CheatFieldMeta
            {
                Id = id, Role = CheatFieldRole.Toggle, Kind = "toggle",
                ToggleDefault = def, GroupPrefix = Prefix(id)
            });
        void Pct(string id, string channel) =>
            list.Add(new CheatFieldMeta
            {
                Id = id, Role = CheatFieldRole.ScalePercent, Kind = "slider",
                Channel = channel, Op = "More", DisplayDefault = 1,
                GroupPrefix = Prefix(id)
            });
        void Flat(string id, string channel) =>
            list.Add(new CheatFieldMeta
            {
                Id = id, Role = CheatFieldRole.ScaleFlat, Kind = "number",
                Channel = channel, Op = "Flat", DisplayDefault = 0,
                GroupPrefix = Prefix(id)
            });
        void Abs(string id, string channel) =>
            list.Add(new CheatFieldMeta
            {
                Id = id, Role = CheatFieldRole.Absolute, Kind = "number",
                Channel = channel, Op = "Override", DisplayDefault = -1,
                GroupPrefix = Prefix(id)
            });
        void Extra(string id, double display = -1) =>
            list.Add(new CheatFieldMeta
            {
                Id = id, Role = CheatFieldRole.Extra, Kind = "number",
                DisplayDefault = display, GroupPrefix = Prefix(id)
            });
        void Cfg(string id, double display, string kind = "number") =>
            list.Add(new CheatFieldMeta
            {
                Id = id, Role = CheatFieldRole.Config, Kind = kind,
                DisplayDefault = display, GroupPrefix = Prefix(id)
            });

        T("A-APPLY", true);
        Pct("A-P-HP%", "hp"); Flat("A-P-HP+", "hp");
        Pct("A-P-ATK%", "atk"); Flat("A-P-ATK+", "atk");
        Pct("A-P-DEF%", "defense"); Flat("A-P-DEF+", "defense");
        Pct("A-Z-HP%", "hp"); Flat("A-Z-HP+", "hp");
        Pct("A-Z-ATK%", "atk"); Flat("A-Z-ATK+", "atk");
        Pct("A-Z-DEF%", "defense"); Flat("A-Z-DEF+", "defense");

        foreach (var id in new[]
                 {
                     "P-GOD", "P-GOD-DIE", "P-DEF-REAL", "P-MOD-HP", "P-MOD-ATK",
                     "Z-GOD", "Z-DEF-BODY", "Z-DEF-APPLY", "Z-REAPPLY-RC",
                     "D-PROBE-PLANT", "D-PROBE-BULLET", "D-HOMING",
                     "F-WAVE-FREEZE", "G-TIMEFREEZE", "G-AUTOCOLLECT", "G-FREE-SET",
                     "H-ANYWHERE", "H-NOCD-CARD", "H-NOCD-GLOVE", "H-NOCD-HAMMER", "H-NOCD-WHEEL", "H-MOWER-INF",
                     "SYS-LIMHEALTH-GATE", "SYS-LIMHEALTH-OBSERVE",
                     "DEBUG-LEVEL-ENTRY"
                 })
            T(id);
        T("SYS-EMIT-PROOF", true);
        T("SYS-DAMAGE-FX", true);
        T("SYS-ELEMENT-FX", true);
        // T8: C1-C13 proved green on a real MelonLoader 3.9 lawn 2026-08-30 (docs/research/
        // effect-runtime/_prove-overlay-combat.json); promoted per spec-overlay-combat-enable.md
        // §7's own "only after the proof" rule.
        T("OVERLAY-COMBAT", true);
        // lawn-combat-wire T10/T12's shared kill switch (lawn-combat-wire-plan.md): registered here so
        // a debug/QA session can still EXPLICITLY override it via /api/cheats/toggle. 2026-09-14
        // correction: LawnBasicAttackFeature.Enabled (FusionRpg.Injector.Effects) no longer reads this
        // entry's ToggleDefault as its production default — that flag now owns a real `DefaultOn`
        // const of its own and treats this schema row as an optional override signal only (see that
        // class's doc comment for why: this registry's job is session cheat/debug state, not a durable
        // feature-flag contract). The `true` below stays accurate for THIS registry's own display/
        // migration semantics (what an unset toggle here should read as if something else ever asks),
        // it is simply no longer load-bearing for the lawn combat feature's default.
        // 2026-09-16 owner decision: the feature is default ON again, so this registry's display
        // default follows it. It was OFF from 2026-09-15 (L-N1) until the perf pass that ruling was
        // conditioned on cleared the 6% ceiling (28.06% of wall at 19.8 fps -> 3.46% at 59.7 fps), now carried by gk-core/data/tuning/lawn-perf-budget.v1.json (lawn LW1.1).
        T("LAWN-BASIC-ATTACK", true);

        Cfg("D-DMG-%", 1, "slider");
        Extra("D-DMG-SET"); Extra("D-TYPE-SWAP");
        Cfg("G-TIMESCALE", 1, "slider");

        foreach (var id in new[] { "E-ZH", "E-ZD", "E-ZS", "E-ZC" })
            Cfg(id, 1, "slider");
        Cfg("E-ZARM", 0);
        Cfg("E-PMIN", 0.2); Cfg("E-PMAX", 6);
        Cfg("E-ZMIN", 0.1); Cfg("E-ZMAX", 10);
        Cfg("E-WAVE-I", 30); Cfg("E-CONV-I", 6);

        Abs("P-HP", "hp"); Abs("P-MAXHP", "maxHp"); Abs("P-ATK", "atk");
        Extra("P-SHIELD"); Extra("P-ATK-INT"); Extra("P-ATK-CD"); Extra("P-ATK-ADD");
        Extra("P-PROD-INT"); Extra("P-PROD-CD"); Extra("P-SPEED"); Extra("P-MOVE");
        Extra("P-LEVEL"); Extra("P-SHOOTLVL"); Extra("P-LIMDMG");

        Abs("Z-HP", "hp"); Abs("Z-MAXHP", "maxHp"); Abs("Z-ATK", "atk");
        Abs("Z-ARM1", "arm1"); Abs("Z-ARM1MAX", "arm1Max");
        Abs("Z-ARM2", "arm2"); Abs("Z-ARM2MAX", "arm2Max");
        Extra("Z-ARMOR-F"); Extra("Z-TAKEMULT"); Extra("Z-SPD-U"); Extra("Z-SPD"); Extra("Z-SPD-O");
        Extra("Z-SLOW-FREEZE"); Extra("Z-SLOW-COLD"); Extra("Z-SLOW-BUTTER");

        All = list;
        ById = list.ToDictionary(f => f.Id, StringComparer.Ordinal);
    }

    static string Prefix(string id)
    {
        var i = id.IndexOf('-');
        return i > 0 ? id[..(i + 1)] : id;
    }

    public static bool TryGet(string id, out CheatFieldMeta meta) => ById.TryGetValue(id, out meta!);

    public static CheatFieldMeta? Get(string id) => ById.TryGetValue(id, out var m) ? m : null;

    /// <summary>Legacy sentinel / identity that must not be treated as an applied user value.</summary>
    public static bool IsUnsetOrIdentity(string id, bool enabled, double floatValue)
    {
        if (!ById.TryGetValue(id, out var m)) return false;
        return m.Role switch
        {
            CheatFieldRole.Toggle => enabled == m.ToggleDefault,
            CheatFieldRole.ScalePercent => Math.Abs(floatValue - 1d) < 0.0000001,
            CheatFieldRole.ScaleFlat => Math.Abs(floatValue) < 0.0000001,
            CheatFieldRole.Absolute => floatValue <= 0,
            CheatFieldRole.Extra => floatValue < 0 || Math.Abs(floatValue - m.DisplayDefault) < 0.0000001 && m.DisplayDefault < 0,
            CheatFieldRole.Config => Math.Abs(floatValue - m.DisplayDefault) < 0.0000001,
            _ => false
        };
    }

    /// <summary>True when this stored value should be stripped from SSOT (treat as absent).</summary>
    public static bool ShouldStripFromDocument(string id, bool enabled, double floatValue, string? kind)
    {
        if (!ById.TryGetValue(id, out var m)) return false;
        if (m.Role == CheatFieldRole.Toggle)
        {
            // Keep toggles that differ from default; strip default-off false and default-on true when "unset" migration of empty docs.
            // For migration of polluted docs: strip only identity scales/absolutes; keep all toggles that were written.
            return false;
        }
        return IsUnsetOrIdentity(id, enabled, floatValue);
    }

    /// <summary>
    /// Strip sentinel/identity float entries from a legacy cheats JSON object (mutates entries list conceptually).
    /// Returns (changed, newEntries).
    /// </summary>
    public static List<Dictionary<string, object?>> MigrateEntries(IEnumerable<Dictionary<string, object?>> entries, out bool changed)
    {
        changed = false;
        var kept = new List<Dictionary<string, object?>>();
        foreach (var e in entries)
        {
            var id = e.TryGetValue("id", out var idObj) ? idObj?.ToString() ?? "" : "";
            var enabled = e.TryGetValue("enabled", out var enObj) && enObj is bool b && b
                          || enObj is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.True;
            double fv = 0;
            if (e.TryGetValue("floatValue", out var fvObj))
            {
                if (fvObj is double d) fv = d;
                else if (fvObj is float f) fv = f;
                else if (fvObj is long l) fv = l;
                else if (fvObj is int i) fv = i;
                else if (fvObj is System.Text.Json.JsonElement jel && jel.TryGetDouble(out var jd)) fv = jd;
                else if (fvObj != null && double.TryParse(fvObj.ToString(), out var p)) fv = p;
            }
            var kind = e.TryGetValue("kind", out var kObj) ? kObj?.ToString() : null;
            if (ShouldStripFromDocument(id, enabled, fv, kind))
            {
                changed = true;
                continue;
            }
            kept.Add(e);
        }
        return kept;
    }

    public static double EffectiveFloat(string id, bool isSet, double stored)
    {
        if (isSet) return stored;
        return ById.TryGetValue(id, out var m) ? m.DisplayDefault : stored;
    }

    public static bool EffectiveToggle(string id, bool isSet, bool stored)
    {
        if (isSet) return stored;
        return ById.TryGetValue(id, out var m) && m.ToggleDefault;
    }
}
