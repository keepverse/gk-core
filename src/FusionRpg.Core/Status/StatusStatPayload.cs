using System.Text.Json;
using FusionRpg.Core.Stats;
using FusionRpg.Core.Stats.Derived;

namespace FusionRpg.Core.Status;

/// <summary>
/// One timed stat contribution a status makes while it is active (E17).
/// </summary>
/// <param name="Op">
/// <c>flat</c> | <c>increased</c> | <c>more</c> — the same vocabulary the modifier bag composes with,
/// so a status contributes through the ordinary phased compose rather than a private path.
/// </param>
public readonly record struct StatusStatMod(string ChannelId, string Op, double Value);

/// <summary>
/// The <c>stat</c> overlay a status carries (spec-status-payload-completion.md, E17).
///
/// <para><b>Four statuses declared this and did nothing.</b> <c>rally</c>, <c>expose</c>,
/// <c>command</c> and <c>shatter</c> all declare <c>StatusPayloadKind.ModifyStat</c>, and that kind
/// had <b>zero consumers repo-wide</b> — they created instances, played VFX, and changed no stat.
/// The key was documented in <c>status-ssot.md</c> and used in a shipped example, and the example
/// <b>failed validation</b>: run against the overlay allowlist it returned <i>"unknown overlay key
/// 'stat' for effect actions"</i>. Documentation of a capability that did not exist.</para>
///
/// <para><b>Timed modifiers are a source-tagged bag entry, withdrawn on expiry</b> — never a direct
/// write. Same law as everything else in this program: one writer, and a contribution that can be
/// taken back.</para>
///
/// <para>Shape: <c>{"atk": {"more": -0.1}, "combat.power.fire": {"flat": 25}}</c> — channel to op to
/// value. A channel outside the primary set or the derived channels is refused rather than parsed
/// into a modifier nothing composes.</para>
/// </summary>
public static class StatusStatPayload
{
    /// <summary>The ops the modifier bag composes. <c>Override</c> is deliberately absent.</summary>
    public static readonly string[] Ops = { "flat", "increased", "more" };

    /// <summary>
    /// Named, so a test can assert against it rather than a substring of a formatted sentence —
    /// mechanism-wiring E2 (spec-mechanism-wiring.md §4.1 sub-decision 3): <c>DerivedModifierOp</c> has
    /// <c>Flat | Increased | Replace | Flag</c> and deliberately no <c>More</c>
    /// (<c>DerivedModifier.cs</c>), so a status authoring <c>more</c> against a derived channel is a
    /// content error, not a runtime detail. Refusing it here — at parse — is the earlier, stricter half
    /// of the same rule <see cref="Stats.Derived.Subsystems.StatusDerivedSubsystem.TryParseOp"/> already
    /// enforces at resolve time by skipping rather than coercing: this stops the bad content from ever
    /// being stored as a withdrawable modifier in the first place.
    /// </summary>
    public const string MoreOnDerivedChannelError =
        "'more' is not valid on a derived channel — there is no `More` op on the derived side";

    /// <summary>
    /// Is <paramref name="channel"/> one of the DERIVED channels (<c>combat.*</c> or the eight
    /// <c>status.power.*</c>/<c>status.resist.*</c> ones) rather than one of the 23 PRIMARY channels?
    ///
    /// <para><b>The one place "derived vs primary" is decided.</b> Both the parser
    /// (<see cref="TryParse"/>, via <see cref="IsKnownChannel"/> and the <c>more</c> refusal below) and
    /// the runtime-side filter (<see cref="StatusDerivedModReader.Read(IReadOnlyList{StatusInstance}?)"/>)
    /// read this SAME predicate, so the two can never independently drift on what "derived" means —
    /// exactly the drift <c>StatusDerivedSubsystem.ContributeDerived</c>'s own doc comment already
    /// warned would be invisible rather than a build error.</para>
    /// </summary>
    public static bool IsDerivedChannel(string channel) =>
        DerivedStatChannels.IsCombatChannel(channel) || DerivedStatusChannels.Contains(channel);

    /// <summary>
    /// Parse the overlay's <c>stat</c> block.
    ///
    /// <para><c>Override</c> is not accepted. A status is a temporary contribution, and an override
    /// replaces the whole channel result — a timed override would silently outrank every permanent
    /// source for its duration, then snap back. Effects cannot emit <c>Override</c> either (E1); this
    /// keeps the one rule in one shape.</para>
    /// </summary>
    public static bool TryParse(
        object? raw, out IReadOnlyList<StatusStatMod> mods, out string? error)
    {
        mods = Array.Empty<StatusStatMod>();
        error = null;

        if (raw is null) return true;

        JsonElement el;
        switch (raw)
        {
            case JsonElement je: el = je; break;
            case string s when !string.IsNullOrWhiteSpace(s):
                try { el = JsonDocument.Parse(s).RootElement.Clone(); }
                catch (JsonException ex) { error = "stat: " + ex.Message; return false; }
                break;
            default:
                error = $"stat: expected an object, got {raw.GetType().Name}";
                return false;
        }

        if (el.ValueKind != JsonValueKind.Object)
        {
            error = $"stat: expected an object, got {el.ValueKind}";
            return false;
        }

        var list = new List<StatusStatMod>();
        foreach (var channel in el.EnumerateObject())
        {
            if (!IsKnownChannel(channel.Name))
            {
                // A channel nothing composes would be a modifier that is created, stored, withdrawn
                // on expiry, and never once read — the silent no-op this whole layer refuses.
                error = $"stat: '{channel.Name}' is not a composed channel";
                return false;
            }

            if (channel.Value.ValueKind != JsonValueKind.Object)
            {
                error = $"stat.{channel.Name}: expected an object of op to value";
                return false;
            }

            foreach (var op in channel.Value.EnumerateObject())
            {
                var opName = op.Name.ToLowerInvariant();
                if (!Array.Exists(Ops, o => o == opName))
                {
                    error = $"stat.{channel.Name}.{op.Name} — one of {string.Join(" | ", Ops)}";
                    return false;
                }

                if (op.Value.ValueKind != JsonValueKind.Number || !op.Value.TryGetDouble(out var value))
                {
                    error = $"stat.{channel.Name}.{op.Name}: expected a number";
                    return false;
                }

                // mechanism-wiring E2: refused at PARSE, never coerced to Flat. `more` is meaningful on
                // a PRIMARY channel (StatSystem's bag composes it); there is no `More` on the derived
                // side, so a status authoring `more` against `combat.*`/`status.*` here is a content
                // error caught before the mod is ever created, stored or withdrawn — not silently
                // dropped later at resolve time.
                if (opName == "more" && IsDerivedChannel(channel.Name))
                {
                    error = $"stat.{channel.Name}.{op.Name}: {MoreOnDerivedChannelError}";
                    return false;
                }

                list.Add(new StatusStatMod(channel.Name, opName, value));
            }
        }

        // Deterministic order: two statuses carrying the same block must produce the same modifiers
        // in the same sequence, or a replay of the same battle would differ by dictionary internals.
        list.Sort((a, b) =>
        {
            var c = string.CompareOrdinal(a.ChannelId, b.ChannelId);
            return c != 0 ? c : string.CompareOrdinal(a.Op, b.Op);
        });

        mods = list;
        return true;
    }

    /// <summary>Primary channels plus the generated combat/status derived set.</summary>
    public static bool IsKnownChannel(string channel) =>
        Array.Exists(StatChannels.All, c => string.Equals(c, channel, StringComparison.Ordinal))
        // E25: O(1) against the cached generation rather than a linear scan of a freshly
        // allocated 84-element list on every channel parsed.
        || IsDerivedChannel(channel);

    static readonly HashSet<string> DerivedStatusChannels = new(StringComparer.Ordinal)
    {
        DerivedStatChannels.StatusPowerOmni,
        DerivedStatChannels.StatusPowerDot,
        DerivedStatChannels.StatusPowerCc,
        DerivedStatChannels.StatusPowerContagion,
        DerivedStatChannels.StatusResistOmni,
        DerivedStatChannels.StatusResistDot,
        DerivedStatChannels.StatusResistCc,
        DerivedStatChannels.StatusResistContagion,
    };

    /// <summary>
    /// The bag entries a live instance contributes, source-tagged so expiry can take them back.
    ///
    /// <para>The source id is the <b>instance</b> id, not the status id: two stacks of the same
    /// status are two contributions, and one expiring must not withdraw the other's.</para>
    /// </summary>
    public static IReadOnlyList<StatModifier> ToModifiers(StatusInstance instance)
    {
        if (instance.StatMods.Count == 0) return Array.Empty<StatModifier>();

        var sourceId = ContributionSourceIds.Status(instance.InstanceId);
        var result = new List<StatModifier>(instance.StatMods.Count);

        foreach (var mod in instance.StatMods)
            result.Add(new StatModifier
            {
                SourceKind = "status",
                SourceId = sourceId,
                PluginId = instance.PluginId ?? "status",
                Channel = mod.ChannelId,
                Op = mod.Op switch
                {
                    "increased" => ModifierOp.Increased,
                    "more" => ModifierOp.More,
                    _ => ModifierOp.Flat,
                },
                Value = mod.Value,
                // E21: StatApplyScope.Matches only recognises the "entity:" grammar — a bare pointer
                // never matches (falls through to the final `return false`), so this contribution
                // silently composed nothing until a seam test proved it end to end through the real
                // StatSystem.Resolve, not just through ToModifiers in isolation.
                ApplyOwnerKey = "entity:" + instance.HostPtr,
            });

        return result;
    }

    /// <summary>What a host withdraws when the instance ends. One id, matching <see cref="ToModifiers"/>.</summary>
    public static string SourceIdOf(StatusInstance instance) =>
        ContributionSourceIds.Status(instance.InstanceId);
}
