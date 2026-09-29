using Xunit;

namespace FusionRpg.Server.Tests;

/// <summary>
/// Serializes the classes that FIGHT over <c>CreatureSpeciesCatalog</c>, which is process-wide.
///
/// <para><b>The defect this closes, measured 2026-09-17.</b>
/// <c>WorldMarchCostProjectionTests.An_unscouted_leys_discount_does_not_apply</c> failed roughly one run
/// in five with <c>Expected: 720, Actual: 576</c> — and only ever inside a full-suite run, never in
/// isolation (0 failures in 6 isolated runs, 2 in ~7 full ones).</para>
///
/// <para><b>Why the number was 576.</b> The legion's banner is resolved by
/// <c>BannerElement.Of</c>, which <i>skips any member whose species the catalog does not know</i>.
/// Its three members — <c>peashooterzombie</c>, <c>conezombie</c>, <c>paperzombie</c> — exist in the
/// COMPILED default (Earth / Ice / Light, so the ring's declared order picks <b>Ice</b>) and are
/// <b>absent from the imported roster</b>, verified by reading every file in
/// <c>gk-data/packs/fusion/data/generated/creatures/</c>. So with the compiled catalog the banner is Ice, no lane endpoint
/// matches, and the ley discount does not apply: 800 × 900‰ = <b>720</b>. With the imported catalog
/// every member is skipped, the banner stops being Ice, and <c>d-flank-1</c>'s believed <c>Earth</c>
/// matches instead — 800 × 900‰ × 800‰ = <b>576</b>.</para>
///
/// <para><b>Who swaps it.</b> <c>PowerAndAptitudeTuningTestBootstrap</c> installs the compiled default
/// once, at assembly load, via <c>[ModuleInitializer]</c>. <c>DelveRoomEncounterTests</c> then calls
/// <c>CreatureSpeciesCatalog.Configure(RealSnapshot.Value)</c> in its CONSTRUCTOR — once per test in
/// that class — replacing it with the imported snapshot for production parity, which that class
/// legitimately needs. Both are correct on their own; the catalog being process-wide is what makes
/// them incompatible when they interleave.</para>
///
/// <para><b>Why a collection rather than a fix in either class.</b> Neither is wrong. The delve class
/// must have the real roster; the world class must have a stable banner. A shared collection makes
/// xUnit run them sequentially, which is the same remedy <c>StructureCatalogSwap</c> already applies in
/// <c>FusionRpg.Data.Tests</c> for a static mass-probe with the identical shape. It is deliberately
/// narrow: only the two classes proven to conflict, rather than every class that happens to read the
/// catalog — serializing on suspicion costs suite time and proves nothing.</para>
///
/// <para><b>What this does NOT claim.</b> Any other class reading species elements while the delve
/// class runs is exposed to the same swap. None has been observed failing, and none is added here on
/// that basis; if one surfaces, it joins this collection with its own evidence.</para>
/// </summary>
[CollectionDefinition(Name)]
public sealed class SpeciesCatalogSwapCollection
{
    public const string Name = "SpeciesCatalogSwap";
}
