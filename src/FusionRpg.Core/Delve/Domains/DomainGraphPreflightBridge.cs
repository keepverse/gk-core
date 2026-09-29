using FusionRpg.Core.Battle;
using FusionRpg.Core.Delve.Roll;
using FusionRpg.Core.Dungeon.Tuning;

namespace FusionRpg.Core.Delve.Domains;

/// <summary>
/// D4.17 row 4's own production bridge (party-dungeon-todo.md, 2026-09-07) — the "small, mechanical
/// remaining step" the row-4 investigation named once real content existed to test it against:
/// <see cref="DomainAnchorBuilder"/> + <see cref="DelveGraphRoll.Roll"/>, wired into
/// <see cref="DomainPreflightInputs.CheckGraphs"/>'s own <c>Func&lt;DomainRow, IReadOnlyList&lt;DomainRefusal&gt;&gt;</c>
/// shape. Deferred earlier the same session only because zero real domains carried a `wild`-kind room
/// (D1.10's own residual) — that gap is closed (12 new wild rooms, all six domain palettes patched),
/// so this bridge now has real content to run against instead of a guaranteed-refuse sweep.
///
/// <para><b>Seed derivation deliberately does NOT reuse <c>string.GetHashCode()</c></b> (the shape
/// <c>RoomPaletteSeedFileTests</c>'s own proving sweep used) — .NET randomizes string hashing per
/// process by default, so a validator built on it would let the SAME content pass preflight in one
/// server run and fail in the next, purely from which of <see cref="DungeonTuning.PreflightSampleSeeds"/>
/// samples got drawn. Spec-domain-catalog.md's own testing strategy requires import to be
/// "byte-identical on rerun" — this uses <see cref="SeededRng.DeriveStream"/> (FNV-1a over a stable
/// stream name), the same deterministic-across-processes mechanism <see cref="DelveStreams"/> already
/// establishes for every other named draw in this module, so a preflight verdict never depends on
/// which process asked.</para>
/// </summary>
public static class DomainGraphPreflight
{
    /// <summary>
    /// One rejection per domain is enough to fail row 4 (spec §2 row 4: "any validator throw") — the
    /// sweep stops at the first sample that throws rather than collecting every one, matching
    /// <see cref="DomainPreflight.Run"/>'s own per-row short-circuit style and avoiding an
    /// O(raidModes × sampleSeeds) refusal flood for one bad domain.
    /// </summary>
    public static Func<DomainRow, IReadOnlyList<DomainRefusal>> Build(
        IReadOnlyDictionary<string, RoomPaletteEntry> roomsById,
        IReadOnlyDictionary<string, IReadOnlyList<string>> roomPaletteByDomainId,
        LayoutTemplateCatalog layouts,
        DungeonTuning tuning)
    {
        if (roomsById is null) throw new ArgumentNullException(nameof(roomsById));
        if (roomPaletteByDomainId is null) throw new ArgumentNullException(nameof(roomPaletteByDomainId));
        if (layouts is null) throw new ArgumentNullException(nameof(layouts));
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        return domain =>
        {
            // Rows 2/3 (run before this one, DomainPreflight.Run's own ordering) already refuse an
            // unknown layoutTemplateId or an empty palette cell; an absent lookup here is unreachable
            // in a real chain and is treated as "nothing to check yet" rather than a second refusal
            // for a defect row 2/3 already named.
            if (!roomPaletteByDomainId.TryGetValue(domain.DomainId, out var palette)) return Array.Empty<DomainRefusal>();
            var layout = layouts.Resolve(domain.LayoutTemplateId);
            if (layout is null) return Array.Empty<DomainRefusal>();

            DomainAnchor anchor;
            try
            {
                anchor = DomainAnchorBuilder.From(domain, roomsById, palette);
            }
            catch (Exception ex) when (ex is KeyNotFoundException or NotSupportedException)
            {
                return new[] { new DomainRefusal(domain.DomainId, "domain.graph:anchor", ex.Message) };
            }

            foreach (var raidMode in layout.RaidModes ?? Array.Empty<string>())
            {
                for (var i = 0; i < tuning.PreflightSampleSeeds; i++)
                {
                    var streamName = $"domain-preflight:{domain.DomainId}:{raidMode}:{i}";
                    var seed = SeededRng.DeriveStream(0, streamName).NextULong();
                    try
                    {
                        DelveGraphRoll.Roll(anchor, layout, seed, raidMode, tuning);
                    }
                    catch (DelveGraphRollRejection ex)
                    {
                        return new[] { new DomainRefusal(domain.DomainId, $"domain.graph:{raidMode}", ex.Message) };
                    }
                }
            }

            return Array.Empty<DomainRefusal>();
        };
    }
}
