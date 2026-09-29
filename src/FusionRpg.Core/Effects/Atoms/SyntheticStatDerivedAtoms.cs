using System.Globalization;
using System.Text.Json;

namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// species-progression SP3.3 (species-layer-projector, map, "one builder"): the synthetic
/// `stat.derived` atom builder moved out of `RpgStore.Species.cs:227-260`
/// (`BuildMagnitudeAtoms`/`Kebab`) so `SpeciesLayerProjector.ToContainer` (module 3) and the
/// species-magnitude synthesizer (`RpgStore.Species.cs`) share exactly one builder — never a
/// second copy of the same flat-amount atom shape in the Data layer.
///
/// <para>Pure Core, no I/O, no validation — validation happens where each caller already validates
/// its own pre-write phase (`AtomRowValidator.Validate`), matching the original's own contract.</para>
/// </summary>
public static class SyntheticStatDerivedAtoms
{
    /// <summary>Pure atom construction for the synthesizer — no I/O, no validation. Byte-identical to
    /// the pre-move `RpgStore.Species.cs` implementation: same atom ids, same params, same ordering.</summary>
    public static IReadOnlyList<(string Channel, AtomRow Atom)> BuildMagnitudeAtoms(
        string speciesId, IReadOnlyDictionary<string, long> magnitudes)
    {
        // Atom families are lowercase kebab (validator rule) while species ids are camelCase with
        // underscores (`CaltropKelp_water`) and channels are camelCase with dots
        // (`resource.max.hp`): flatten both to dashes and lowercase, deterministically — the two
        // namespaces meet only inside this synthesizer, both ways, and the container id keeps the
        // raw species id (the consumer's join key, never sanitized).
        var family = $"atom.species-magnitude-{Kebab(speciesId)}";
        return magnitudes.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv =>
            {
                var slug = Kebab(kv.Key);
                var atom = new AtomRow
                {
                    AtomId = AtomRow.DeriveId(family, slug, 1),
                    KindId = "stat.derived",
                    FamilyId = family,
                    Variant = slug,
                    Tier = 1,
                    Name = family,
                    ParamsJson = "{\"channel\":" + JsonSerializer.Serialize(kv.Key)
                        + ",\"op\":\"flat\",\"amount\":" + kv.Value.ToString(CultureInfo.InvariantCulture) + "}",
                    WhenJson = "{}",
                };
                return (kv.Key, atom);
            })
            .ToList();
    }

    public static string Kebab(string value) => value
        .ToLowerInvariant()
        .Replace(".", "-", StringComparison.Ordinal)
        .Replace("_", "-", StringComparison.Ordinal);
}
