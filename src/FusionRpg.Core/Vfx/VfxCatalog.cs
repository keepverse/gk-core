namespace FusionRpg.Core.Vfx;

/// <summary>Cue id → recipe SSOT. Replace-whole-entry semantics only (vfx-ssot.md §6.2).</summary>
public sealed class VfxCatalog
{
    readonly Dictionary<string, VfxRecipe> _byId = new(StringComparer.OrdinalIgnoreCase);

    public void ReplaceAll(IEnumerable<VfxRecipe> recipes)
    {
        if (recipes == null) throw new ArgumentNullException(nameof(recipes));
        var next = new Dictionary<string, VfxRecipe>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in recipes)
        {
            Validate(r);
            next[r.CueId] = r;
        }

        _byId.Clear();
        foreach (var kv in next) _byId[kv.Key] = kv.Value;
    }

    public bool TryGet(string? cueId, out VfxRecipe recipe)
    {
        if (!string.IsNullOrWhiteSpace(cueId) && _byId.TryGetValue(cueId, out var r))
        {
            recipe = r;
            return true;
        }

        recipe = null!;
        return false;
    }

    public IReadOnlyList<string> Ids => _byId.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToList();

    public static void Validate(VfxRecipe recipe)
    {
        if (recipe == null) throw new ArgumentNullException(nameof(recipe));
        if (string.IsNullOrWhiteSpace(recipe.CueId))
            throw new ArgumentException("recipe.CueId is required");
        if (recipe.Primitives == null || recipe.Primitives.Count == 0)
            throw new ArgumentException(recipe.CueId + ": recipe needs at least one primitive");
        foreach (var p in recipe.Primitives)
        {
            if (p == null) throw new ArgumentException(recipe.CueId + ": null primitive spec");
            if (p.Kind is VfxPrimitiveKind.ImpactStamp or VfxPrimitiveKind.Charge or VfxPrimitiveKind.Travel)
            {
                // Stamp timing, span and count are presentation tuning, never recipe literals.
                if (!p.RequireElement)
                    throw new ArgumentException(recipe.CueId + ": Earth art requires an element");
                if (p.Color != VfxColorSourceKind.TagOrElement)
                    throw new ArgumentException(recipe.CueId + ": Earth art uses element selection");
                if (p.LifeSeconds != 0f || p.SizeScale != 1f || p.Count != 1 || p.DelaySeconds != 0f)
                    throw new ArgumentException(recipe.CueId + ": Earth art presentation is configured in VFX tuning");
                continue;
            }
            // Sustained kinds live apply→expire; LifeSeconds is meaningless for them.
            if (!p.IsSustained && p.LifeSeconds <= 0f)
                throw new ArgumentException(recipe.CueId + ": primitive LifeSeconds must be > 0");
            if (p.Count < 1 || p.Count > 64)
                throw new ArgumentException(recipe.CueId + ": primitive Count must be 1..64");
            if (p.DelaySeconds < 0f)
                throw new ArgumentException(recipe.CueId + ": primitive DelaySeconds must be >= 0");
        }
    }
}

/// <summary>Cue id vocabulary — vfx-ssot.md §4.</summary>
public static class VfxCueIds
{
    public const string CombatHit = "combat.hit";
    public const string CombatHeal = "combat.heal";
    public const string DebugProbe = "debug.probe";
    /// <summary>Shield pool broke (shield-system-spec.md §2.6) — art/tuning owned by the VFX stream.</summary>
    public const string ShieldBroken = "shield.broken";
    public const string RiftPortalOpen = "rift.portal.open";
    public const string RiftPortalSurge = "rift.portal.surge";
    public const string RiftQuarantineSeal = "rift.quarantine.seal";
    public const string RiftQuarantineFade = "rift.quarantine.fade";
}

/// <summary>
/// C#-seeded VFX cue catalog (vfx-ssot.md §6.2).
///
/// <para>This said it mirrored the effect-def catalog. It does not, and never did — every row here
/// keys on a <b>statusId</b>, and the file contains no <c>fx.*</c> id at all. E11 was scheduled
/// around a coupling that turned out to be a sentence. A comment is not evidence.</para>
/// </summary>
public static class VfxSeedCatalog
{
    /// <summary>Legacy world-flash orange — the pre-VFX-SSOT burst color, kept for continuity.</summary>
    public static readonly (byte R, byte G, byte B) ProbeOrange = (255, 128, 20);

    /// <summary>
    /// statusId → apply-cue color, one row per catalog status (every one has prove coverage,
    /// SSOT §4.2 criterion). Adding a status VFX = one row here + the status emitting normally.
    /// **This array drifted from <see cref="FusionRpg.Core.Status.StatusCategoryRegistry"/> once
    /// already (D17, backlog-clean-up BCU8.1) — a join-closure test asserts every
    /// <c>StatusCategoryRegistry.AllStatusIds</c> entry has a row here, never a pinned count.**
    /// </summary>
    public static readonly (string Id, byte R, byte G, byte B)[] StatusFx =
    {
        ("butter", 255, 220, 80), ("freeze", 120, 200, 255), ("cold", 170, 220, 255),
        ("poison", 110, 200, 60), ("hypno", 230, 90, 200), ("ember", 255, 120, 50),
        ("jala", 255, 70, 40), ("kelp", 60, 180, 140), ("wither", 140, 110, 90),
        ("bond", 255, 170, 200), ("rally", 255, 200, 90), ("leech", 180, 60, 60),
        ("expose", 250, 250, 140), ("command", 120, 140, 255), ("shatter", 200, 230, 255),
        ("charm_pulse", 240, 120, 240), ("blight", 130, 160, 60), ("rot", 120, 90, 50),
        ("spark", 255, 240, 120), ("pact_mark", 170, 90, 220), ("spore", 150, 200, 90),
        // combat-math-dedup D17 (backlog-clean-up BCU8.1, 2026-09-20): the three
        // status-ssot.md §9.5 nerve tiers (delve-attrition D2.19) never had an apply cue, so this
        // array silently drifted to 21 rows against the registry's 24 — the comment above claiming
        // "one row per catalog status" was false. Colors escalate a grey-violet dread palette across
        // the three tiers (unsettled -> shaken -> afflicted), matching no other status's hue.
        ("nerve.unsettled", 150, 140, 160), ("nerve.shaken", 120, 90, 150), ("nerve.afflicted", 90, 30, 110)
    };

    /// <summary>One status's while-active composition — aura and/or tint and/or marker (SPEC §4).</summary>
    public sealed record StatusSustain(
        string Id,
        VfxAuraStyle? Aura = null, byte AR = 0, byte AG = 0, byte AB = 0,
        float Tint = 0f, byte TR = 0, byte TG = 0, byte TB = 0,
        VfxMarkerShape? Marker = null, byte MR = 0, byte MG = 0, byte MB = 0);

    /// <summary>
    /// statusId → sustained composition — all 13 custom statuses (vfx-v3, SPEC §4).
    /// Grammar: Drip = generic DoT fallback; WispOut/BubbleRise/ChunkFall = batch-1 drip-cluster
    /// identity (wither/blight/rot). SparkStrobe/ShardGlitter = batch-2 crackle-cluster identity
    /// (spark/shatter). SporeDrift/CharmHeartbeat = batch-3 orbit-cluster identity (spore/charm_pulse).
    /// PactFootPulse/CommandCrownPulse = batch-5 pulsering-cluster identity (pact_mark/command).
    /// Orbit = generic passive/link fallback (bond). RiseSparkle = buff, PulseRing = active mark fallback;
    /// Engine-wrapped vanilla statuses never appear here — original visuals untouched.
    /// </summary>
    public static readonly StatusSustain[] StatusSustainFx =
    {
        new("wither", Aura: VfxAuraStyle.WispOut, AR: 150, AG: 120, AB: 95,
            Tint: 0.25f, TR: 140, TG: 130, TB: 120),
        new("blight", Aura: VfxAuraStyle.BubbleRise, AR: 130, AG: 180, AB: 60,
            Tint: 0.20f, TR: 110, TG: 170, TB: 80),
        new("rot", Aura: VfxAuraStyle.ChunkFall, AR: 120, AG: 90, AB: 50,
            Tint: 0.20f, TR: 130, TG: 100, TB: 70),
        new("spark", Aura: VfxAuraStyle.SparkStrobe, AR: 255, AG: 240, AB: 120),
        new("spore", Aura: VfxAuraStyle.SporeDrift, AR: 150, AG: 220, AB: 90),
        new("pact_mark", Aura: VfxAuraStyle.PactFootPulse, AR: 170, AG: 90, AB: 220,
            Marker: VfxMarkerShape.Diamond, MR: 170, MG: 90, MB: 220),
        new("leech", Aura: VfxAuraStyle.StreamOut, AR: 180, AG: 60, AB: 60,
            Tint: 0.15f, TR: 180, TG: 60, TB: 60),
        new("expose", Aura: VfxAuraStyle.CrackleJitter, AR: 250, AG: 210, AB: 90,
            Marker: VfxMarkerShape.TriangleDown, MR: 250, MG: 210, MB: 90),
        new("shatter", Aura: VfxAuraStyle.ShardGlitter, AR: 200, AG: 235, AB: 255,
            Tint: 0.15f, TR: 180, TG: 220, TB: 255),
        new("bond", Aura: VfxAuraStyle.Orbit, AR: 255, AG: 170, AB: 200,
            Marker: VfxMarkerShape.Ring, MR: 255, MG: 170, MB: 200),
        new("rally", Aura: VfxAuraStyle.RiseSparkle, AR: 255, AG: 200, AB: 90,
            Tint: 0.10f, TR: 255, TG: 215, TB: 150),
        new("command", Aura: VfxAuraStyle.CommandCrownPulse, AR: 120, AG: 140, AB: 255,
            Marker: VfxMarkerShape.Ring, MR: 120, MG: 140, MB: 255),
        new("charm_pulse", Aura: VfxAuraStyle.CharmHeartbeat, AR: 240, AG: 120, AB: 240,
            Tint: 0.15f, TR: 240, TG: 120, TB: 240)
    };

    static float AuraSizeScale(string statusId) => statusId switch
    {
        "wither" => 0.9f,
        "rot" => 1.25f,
        "expose" => 0.85f,
        "spore" => 1.15f,
        "pact_mark" => 0.9f,
        "command" => 1.05f,
        _ => 1f
    };

    static VfxPrimitiveSpec StatusApplyBurst(string statusId, byte r, byte g, byte b) =>
        statusId.ToLowerInvariant() switch
        {
            "wither" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.35f,
                Count = 10,
                Shape = VfxBurstShape.Radial
            },
            "blight" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.5f,
                Count = 12,
                Shape = VfxBurstShape.Rising
            },
            "rot" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.45f,
                Count = 8,
                Shape = VfxBurstShape.Radial,
                SizeScale = 1.35f
            },
            "spark" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.30f,
                Count = 16,
                Shape = VfxBurstShape.Radial
            },
            "shatter" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.40f,
                Count = 10,
                Shape = VfxBurstShape.Directional,
                SizeScale = 1.25f
            },
            "expose" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.40f,
                Count = 10,
                Shape = VfxBurstShape.Rising
            },
            "spore" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.45f,
                Count = 12,
                Shape = VfxBurstShape.Rising
            },
            "charm_pulse" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.35f,
                Count = 14,
                Shape = VfxBurstShape.Radial,
                SizeScale = 0.9f
            },
            "bond" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.40f,
                Count = 10,
                Shape = VfxBurstShape.Radial
            },
            "leech" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.40f,
                Count = 10,
                Shape = VfxBurstShape.Directional
            },
            "rally" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.50f,
                Count = 13,
                Shape = VfxBurstShape.Rising,
                SizeScale = 1.05f
            },
            "pact_mark" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.30f,
                Count = 12,
                Shape = VfxBurstShape.Radial,
                SizeScale = 1.1f
            },
            "command" => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.35f,
                Count = 10,
                Shape = VfxBurstShape.Radial,
                SizeScale = 0.95f
            },
            _ => new VfxPrimitiveSpec
            {
                Kind = VfxPrimitiveKind.Burst,
                Color = VfxColorSourceKind.Fixed,
                FixedRgb = (r, g, b),
                LifeSeconds = 0.45f,
                Count = 14,
                Shape = VfxBurstShape.Radial
            }
        };

    public static List<VfxRecipe> CreateAll()
    {
        var all = CoreRecipes();
        foreach (var (id, r, g, b) in StatusFx)
        {
            var specs = new List<VfxPrimitiveSpec>
            {
                StatusApplyBurst(id, r, g, b),
                new()
                {
                    Kind = VfxPrimitiveKind.Flash,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (r, g, b),
                    LifeSeconds = 0.18f
                }
            };
            foreach (var s in StatusSustainFx)
            {
                if (!string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)) continue;
                if (s.Aura.HasValue)
                {
                    specs.Add(new VfxPrimitiveSpec
                    {
                        Kind = VfxPrimitiveKind.Aura,
                        AuraStyle = s.Aura.Value,
                        Color = VfxColorSourceKind.Fixed,
                        FixedRgb = (s.AR, s.AG, s.AB),
                        SizeScale = AuraSizeScale(id)
                    });
                }

                if (s.Tint > 0f)
                {
                    specs.Add(new VfxPrimitiveSpec
                    {
                        Kind = VfxPrimitiveKind.Tint,
                        Color = VfxColorSourceKind.Fixed,
                        FixedRgb = (s.TR, s.TG, s.TB),
                        TintStrength = s.Tint
                    });
                }

                if (s.Marker.HasValue)
                {
                    specs.Add(new VfxPrimitiveSpec
                    {
                        Kind = VfxPrimitiveKind.Marker,
                        MarkerShape = s.Marker.Value,
                        Color = VfxColorSourceKind.Fixed,
                        FixedRgb = (s.MR, s.MG, s.MB)
                    });
                }
            }

            all.Add(new VfxRecipe
            {
                CueId = StatusVfxCues.CueId(id),
                Primitives = specs
            });
        }

        return all;
    }

    static List<VfxRecipe> CoreRecipes() => new()
    {
        new VfxRecipe
        {
            CueId = VfxCueIds.CombatHit,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Floater,
                    Label = VfxLabelSourceKind.TagAmount,
                    LifeSeconds = VfxRules.FloaterLifeSeconds
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 28,
                    RequireElement = true
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Flash,
                    LifeSeconds = 0.18f,
                    RequireElement = true
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.ImpactStamp,
                    RequireElement = true
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Charge,
                    RequireElement = true
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Travel,
                    RequireElement = true
                }
            }
        },
        new VfxRecipe
        {
            CueId = VfxCueIds.CombatHeal,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Floater,
                    Label = VfxLabelSourceKind.TagAmount,
                    LifeSeconds = VfxRules.FloaterLifeSeconds
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    Shape = VfxBurstShape.Rising,
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 14
                }
            }
        },
        new VfxRecipe
        {
            CueId = VfxCueIds.ShieldBroken,
            Primitives = new[]
            {
                // Placeholder shatter: pale-blue burst + flash; the VFX stream owns final art.
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (200, 230, 255),
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 20
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Flash,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (200, 230, 255),
                    LifeSeconds = 0.15f
                }
            }
        },
        new VfxRecipe
        {
            CueId = VfxCueIds.DebugProbe,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = ProbeOrange,
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 28
                }
            }
        },
        new VfxRecipe
        {
            CueId = VfxCueIds.RiftPortalOpen,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (210, 100, 255),
                    Shape = VfxBurstShape.Radial,
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 24,
                    SizeScale = 1.25f
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Flash,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (165, 70, 255),
                    LifeSeconds = 0.22f
                }
            }
        },
        new VfxRecipe
        {
            CueId = VfxCueIds.RiftPortalSurge,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (190, 255, 70),
                    Shape = VfxBurstShape.Directional,
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 32,
                    SizeScale = 1.35f
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Flash,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (225, 255, 120),
                    LifeSeconds = 0.18f
                }
            }
        },
        new VfxRecipe
        {
            CueId = VfxCueIds.RiftQuarantineSeal,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (120, 220, 255),
                    Shape = VfxBurstShape.Directional,
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 20,
                    SizeScale = 1.1f
                },
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Flash,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (180, 240, 255),
                    LifeSeconds = 0.2f
                }
            }
        },
        new VfxRecipe
        {
            CueId = VfxCueIds.RiftQuarantineFade,
            Primitives = new[]
            {
                new VfxPrimitiveSpec
                {
                    Kind = VfxPrimitiveKind.Burst,
                    Color = VfxColorSourceKind.Fixed,
                    FixedRgb = (150, 120, 220),
                    Shape = VfxBurstShape.Rising,
                    LifeSeconds = VfxRules.BurstLifeSeconds,
                    Count = 14,
                    SizeScale = 0.9f
                }
            }
        }
    };
}
