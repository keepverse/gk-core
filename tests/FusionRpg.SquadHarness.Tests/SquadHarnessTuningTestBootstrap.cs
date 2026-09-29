using System.Runtime.CompilerServices;
using FusionRpg.Tools.SquadHarness;

namespace FusionRpg.SquadHarness.Tests;

/// <summary>
/// Configures the tuning hubs once for this whole assembly, matching
/// <c>FusionRpg.Core.Tests</c>'s and <c>FusionRpg.Data.Tests</c>'s own
/// <c>ContractTuningTestBootstrap</c> shape.
///
/// <para><b>Why this exists.</b> Every test here used to call <see cref="TuningBootstrap.Configure"/>
/// by hand, and three classes — <c>AggregationTests</c>, <c>CoverageTests</c> and
/// <c>ResolutionTests</c> — never called it at all. The hubs are static, so whether a test saw a
/// configured hub depended on which collection xUnit happened to run first: an **order-dependent**
/// failure, not a deterministic one. Measured 2026-09-17 before the fix: <b>73 of 193 failing</b>,
/// 62 with <c>BattleRuleset: no 'atk' entry in data/tuning/power-scale.v{n}.json's channels block</c>
/// and 9 with <c>ChannelAnchor.UnknownChannelPin</c> for <c>combat.power.omni</c>.</para>
///
/// <para><b>Neither message meant what it said.</b> The shipped tuning is fine — both
/// <c>power-scale.v1.json</c> and <c>v2.json</c> carry a <c>channels</c> block containing <c>atk</c>,
/// and <c>ChannelAnchor.AnchorFamilyOf</c> maps every <c>combat.power.*</c> channel onto that same
/// <c>atk</c> family. The throw was <c>ChannelsOrEmpty</c> being <b>empty</b>, i.e. the hub never
/// configured in this assembly. There was no missing pin and nothing to publish, which matters
/// because publishing one would have been a balance change this program does not make.</para>
///
/// <para>⚠️ Do not reintroduce a per-test <c>Configure()</c> call. A module initializer runs once
/// before any test in the assembly, so a per-test call adds nothing — and leaving them in is exactly
/// how the next new class forgets one and reintroduces the order dependence.</para>
/// </summary>
internal static class SquadHarnessTuningTestBootstrap
{
    [ModuleInitializer]
    public static void Init() => TuningBootstrap.Configure();
}
