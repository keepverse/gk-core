using FusionRpg.Core.Combat.Element;
using FusionRpg.Core.Effects.Atoms.Power;
using FusionRpg.Core.Stats;

namespace FusionRpg.Data;

/// <summary>
/// Loads the store's content tables into the Core statics that actually get read (E20,
/// completeness-audit.md finding A2).
///
/// <para><b>Every table the atom layer writes had a reader in its own tests and none in production.</b>
/// <c>ElementTable.Current</c> and <c>PowerTables.Current</c> default to a shipped code copy and stay
/// on it forever unless a host calls <c>Use</c> — and until this method existed, nothing did. Editing
/// an imported roster row or coefficient moved the content hash and changed no composed number.</para>
///
/// <para>Call once, after <see cref="RpgStore.Init"/>, from a process that wants live content — today
/// that is the server. A process that never calls this runs on the shipped fallback, exactly as it
/// always has; this method only ever <i>adds</i> a source of truth, never removes the default one.</para>
/// </summary>
public sealed partial class RpgStore
{
    public void LoadContentIntoRuntime()
    {
        ElementTable.Use(GetElementTable());
        PowerTables.Use(GetPowerTables());

        // E22: only the direction column has a live consumer (StatChannels.IsLowerBetter) — see
        // ChannelPolicyTable's doc comment for why default/cap/composeKind stay unread.
        var directions = GetChannelPolicies()
            .ToDictionary(r => r.ChannelId, r => r.Direction, StringComparer.Ordinal);
        ChannelPolicyTable.Use(new ChannelPolicyTable(directions));
    }

    /// <summary>"imported" once the catalog tables genuinely hold content; "codeFallback" while a
    /// player has never had a successful import (E46, player-content-boot). Read by
    /// <see cref="ToHealth"/> — see <see cref="RecordContentBootOutcome"/> for who sets it.</summary>
    public string ContentSource { get; private set; } = "codeFallback";

    /// <summary>Why the self-healing startup import did not run or did not succeed. Null once content
    /// is imported.</summary>
    public string? ContentImportError { get; private set; }

    /// <summary>
    /// Records what the server's self-healing startup import (E46) did, so <c>/health</c> can report
    /// it. Called exactly once, right after <c>FusionRpg.Data.Seed.SeedImportRunner.RunSelfHealing</c>
    /// and before <see cref="LoadContentIntoRuntime"/> — nothing else should call this.
    ///
    /// <para>The whole point of E46 is that an absent import must stop looking identical to a
    /// successful one (spec-player-content-boot.md §3.2). Before this existed, a fresh player install
    /// booted on the code fallback with no table anywhere saying so.</para>
    /// </summary>
    public void RecordContentBootOutcome(string source, string? error)
    {
        ContentSource = source;
        ContentImportError = error;
    }
}
