namespace FusionRpg.CheatCore;

public sealed class CheatEntryDto
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "toggle";
    public bool Enabled { get; set; }
    public double FloatValue { get; set; }
}

/// <summary>Unity-free cheat registry for CI and shared defaults.</summary>
public sealed class CheatRegistry
{
    readonly object _gate = new();
    readonly Dictionary<string, CheatEntryDto> _entries = new(StringComparer.Ordinal);

    public bool BoardConfigLocked { get; set; }

    public IReadOnlyDictionary<string, CheatEntryDto> Entries
    {
        get { lock (_gate) return _entries.ToDictionary(kv => kv.Key, kv => Clone(kv.Value)); }
    }

    public void EnsureDefaults()
    {
        lock (_gate)
        {
            void T(string id, bool v = false) => Put(id, new CheatEntryDto { Id = id, Kind = "toggle", Enabled = v });
            void F(string id, double v) => Put(id, new CheatEntryDto { Id = id, Kind = "slider", FloatValue = v, Enabled = true });
            void N(string id, double v) => Put(id, new CheatEntryDto { Id = id, Kind = "number", FloatValue = v, Enabled = true });

            T("A-APPLY", true);
            F("A-P-HP%", 1f); N("A-P-HP+", 0);
            F("A-P-ATK%", 1f); N("A-P-ATK+", 0);
            F("A-P-DEF%", 1f); N("A-P-DEF+", 0);
            F("A-Z-HP%", 1f); N("A-Z-HP+", 0);
            F("A-Z-ATK%", 1f); N("A-Z-ATK+", 0);
            F("A-Z-DEF%", 1f); N("A-Z-DEF+", 0);

            foreach (var id in new[]
                     {
                         "P-GOD", "P-GOD-DIE", "P-DEF-REAL", "P-MOD-HP", "P-MOD-ATK",
                         "Z-GOD", "Z-DEF-BODY", "Z-DEF-APPLY", "Z-REAPPLY-RC",
                         "D-PROBE-PLANT", "D-PROBE-BULLET", "D-HOMING",
                         "F-WAVE-FREEZE", "G-TIMEFREEZE", "G-AUTOCOLLECT", "G-FREE-SET",
                         "H-ANYWHERE", "H-NOCD-CARD", "H-NOCD-GLOVE", "H-NOCD-HAMMER", "H-NOCD-WHEEL", "H-MOWER-INF",
                         "SYS-EMIT-PROOF", "SYS-DAMAGE-FX", "SYS-ELEMENT-FX",
                         "SYS-LIMHEALTH-GATE", "SYS-LIMHEALTH-OBSERVE",
                         "OVERLAY-COMBAT", "DEBUG-LEVEL-ENTRY", "LAWN-BASIC-ATTACK"
                     })
                T(id);

            F("D-DMG-%", 1f); N("D-DMG-SET", -1);
            N("D-TYPE-SWAP", -1);
            F("G-TIMESCALE", 1f);

            foreach (var id in new[] { "E-ZH", "E-ZD", "E-ZS", "E-ZC" })
                F(id, 1f);
            N("E-ZARM", 0);
            F("E-PMIN", 0.2f); F("E-PMAX", 6f);
            F("E-ZMIN", 0.1f); F("E-ZMAX", 10f);
            N("E-WAVE-I", 30); N("E-CONV-I", 6);

            N("P-HP", -1); N("P-MAXHP", -1); N("P-SHIELD", -1); N("P-ATK", -1);
            N("P-ATK-INT", -1); N("P-ATK-CD", -1); N("P-ATK-ADD", -1);
            N("P-PROD-INT", -1); N("P-PROD-CD", -1); N("P-SPEED", -1); N("P-MOVE", -1);
            N("P-LEVEL", -1); N("P-SHOOTLVL", -1); N("P-LIMDMG", -1);

            N("Z-HP", -1); N("Z-MAXHP", -1); N("Z-ARM1", -1); N("Z-ARM1MAX", -1);
            N("Z-ARM2", -1); N("Z-ARM2MAX", -1); N("Z-ATK", -1); N("Z-ARMOR-F", -1);
            N("Z-TAKEMULT", -1); N("Z-SPD-U", -1); N("Z-SPD", -1); N("Z-SPD-O", -1);
            N("Z-SLOW-FREEZE", -1); N("Z-SLOW-COLD", -1); N("Z-SLOW-BUTTER", -1);

            Get("SYS-EMIT-PROOF").Enabled = true;
            Get("SYS-DAMAGE-FX").Enabled = true;
            Get("SYS-ELEMENT-FX").Enabled = true;
            // T8: C1-C13 proved green on a real MelonLoader 3.9 lawn 2026-08-30 (docs/research/
            // effect-runtime/_prove-overlay-combat.json); promoted per spec-overlay-combat-enable.md
            // §7's own "only after the proof" rule.
            Get("OVERLAY-COMBAT").Enabled = true;
            // lawn-combat-wire T10/T12's shared kill switch — default ON (owner decision 2026-09-16).
            // It was OFF from 2026-09-15 (L-N1) on a measured 300-zombie frame-budget breach, which
            // named its own release condition: "until a perf pass clears it". The pass ran — the RPG
            // capture pipeline went from 28.06% of wall at 19.8 fps to 3.46% at 59.7 fps, under the
            // ceiling now in gk-core/data/tuning/lawn-perf-budget.v1.json (lawn LW1.1); this registry's default follows.
            Get("LAWN-BASIC-ATTACK").Enabled = true;
        }
    }

    void Put(string id, CheatEntryDto e)
    {
        if (!_entries.ContainsKey(id)) _entries[id] = e;
    }

    public CheatEntryDto Get(string id)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(id, out var e))
            {
                e = new CheatEntryDto { Id = id, Kind = "toggle" };
                _entries[id] = e;
            }
            return e;
        }
    }

    public bool On(string id) => Get(id).Enabled;
    public double FVal(string id) => Get(id).FloatValue;

    public void SetToggle(string id, bool on)
    {
        Get(id).Enabled = on;
    }

    public void SetFloat(string id, double v)
    {
        var e = Get(id);
        e.FloatValue = v;
        e.Enabled = true;
        if (id.StartsWith("E-", StringComparison.Ordinal)) BoardConfigLocked = true;
    }

    public void ResetAll()
    {
        lock (_gate) _entries.Clear();
        EnsureDefaults();
        BoardConfigLocked = false;
    }

    public void ResetGroup(string prefix)
    {
        lock (_gate)
        {
            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                _entries.Remove(key);
        }
        EnsureDefaults();
    }

    public Dictionary<string, object> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, object>
            {
                ["boardConfigLocked"] = BoardConfigLocked,
                ["entries"] = _entries.Values.Select(e => new Dictionary<string, object>
                {
                    ["id"] = e.Id,
                    ["kind"] = e.Kind,
                    ["enabled"] = e.Enabled,
                    ["floatValue"] = e.FloatValue
                }).ToList()
            };
        }
    }

    public void ApplySnapshot(IEnumerable<(string id, bool enabled, double floatValue)> entries, bool? boardLocked = null)
    {
        foreach (var (id, enabled, floatValue) in entries)
        {
            var e = Get(id);
            e.Enabled = enabled;
            e.FloatValue = floatValue;
        }
        if (boardLocked is { } b) BoardConfigLocked = b;
        else BoardConfigLocked = true;
    }

    static CheatEntryDto Clone(CheatEntryDto e) => new()
    {
        Id = e.Id,
        Kind = e.Kind,
        Enabled = e.Enabled,
        FloatValue = e.FloatValue
    };
}
