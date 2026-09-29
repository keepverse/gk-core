using FusionRpg.Core.Effects.Atoms;

namespace FusionRpg.Core.Actions.Ai;

/// <summary>
/// combat-ai `replay-identity` (module 8, CAI2.1, spec-replay-identity.md §1): the identity of one
/// published profile set, as a <see cref="ContentHashStamp"/> — **reused, never forked**. The spec's
/// own §1 is explicit about why: `ContentHashStamp` is already a part-agnostic
/// `(SchemaVersion, Hash, IReadOnlyDictionary&lt;string,string&gt;)`, its compact form is a durable wire
/// format, and `ContentHashComparison.Compare` is already the exact four-verdict decision procedure this
/// module needs (null = Match, same version + different hash = Mismatch, different version =
/// RegistryChanged, unparseable = Unreadable). A second stamp type with those same verdicts is the
/// mechanism fork the repo's SOLID rule bans.
///
/// <para><b>The version is the publish counter.</b> <see cref="ContentHashStamp.SchemaVersion"/> carries
/// `CombatAiTuning.Version` — the `v{n}` of `data/tuning/combat-ai.v{n}.json`, the same number
/// <c>ForVersion</c> is asked for — not the file's shape `SchemaVersion`. That is what makes the pin
/// addressable: a stamp that named the shape could not say which published set a match resolved under.
/// It also makes the two comparison criteria land where the spec puts them: a profile **added** is a
/// publish (a new `v{n+1}`), so the version moves and the verdict is
/// <see cref="ContentHashVerdict.RegistryChanged"/> — not a refusal, because a new place&#215;role must
/// not strand every earlier match; a profile **edited in place under its own version** is a
/// <see cref="ContentHashVerdict.Mismatch"/>, naming that profile id in
/// <see cref="ContentHashComparison.ChangedTables"/>, which is exactly the hand-edit ban turning into a
/// detection (`tunables-ssot.md`; `AGENTS.md` "Generated seed data is never hand-edited").</para>
///
/// <para><b>Pure.</b> It reads no file and no clock. <see cref="ICombatAiProfileSource"/> is the
/// host-side seam that supplies an already-parsed set; `profile-schema` owns the parser.</para>
///
/// <para><b>Deviation from the spec's own snippet, stated so a reviewer does not treat it as a
/// typo.</b> The spec writes <c>StampOf(CombatAiProfileSet set)</c> and a
/// <c>CombatAiProfileSet</c> type. Module 2 shipped <see cref="CombatAiTuning"/>, which already IS that
/// set — it carries <c>Version</c> and the <c>Profiles</c> map — so this module stamps
/// <see cref="CombatAiTuning"/> rather than declaring a second wrapper type with the same two fields
/// (the same "one owner per number" reasoning CAI1.14 applied to `PerActorState`).</para>
/// </summary>
public static class CombatAiProfileIdentity
{
    /// <summary>
    /// The identity of one published profile set: the tuning version, the combined digest over every
    /// profile, and a per-profile digest so a refusal can name WHICH profile moved.
    ///
    /// <para>Both levels hash through <see cref="ContentHash"/>'s own aggregation rules — the canonical
    /// form is length-prefixed (a bare separator is not injective), and
    /// <see cref="ContentHash.TableDigest"/> sorts its inputs, so the combined digest is
    /// **order-independent** exactly as <c>ToCompact</c> and <c>ComputeContentHash</c> already are.</para>
    /// </summary>
    public static ContentHashStamp StampOf(CombatAiTuning tuning)
    {
        if (tuning is null) throw new ArgumentNullException(nameof(tuning));

        var digests = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = new List<byte[]>(tuning.Profiles.Count);

        // Sorted, explicitly: `ContentHash.TableDigest` sorts too, but the per-profile DIGEST MAP must
        // be built in a fixed order as well — dictionary enumeration order is not a contract, so the
        // canonical input is ordered here rather than trusted (the same discipline `ToCompact` applies
        // to its parts, and the thing the Core/Actions purity scan's dictionary rule protects).
        foreach (var pair in tuning.Profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var row = System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Canonical(pair.Value)));
            rows.Add(row);
            digests[pair.Key] = ContentHash.Hex(ContentHash.TableDigest(new[] { row }));
        }

        return new ContentHashStamp(tuning.Version, ContentHash.Hex(ContentHash.TableDigest(rows)), digests);
    }

    /// <summary>Compare a stored compact stamp with the set a replay is about to use. Delegates verbatim
    /// to <see cref="ContentHashComparison.Compare"/> — same four verdicts, same pre-field NULL rule,
    /// same `ShouldRefuse`. There is no second decision procedure here.</summary>
    public static ContentHashComparison Compare(string? storedCompact, ContentHashStamp current) =>
        ContentHashComparison.Compare(storedCompact, current);

    /// <summary>
    /// One profile's canonical form: every public member, by name, length-prefixed, with dictionaries
    /// ordered by key and lists kept in their own order (a profile row's INDEX is its rank, so list
    /// order is meaning, not noise).
    ///
    /// <para><b>Reflection, deliberately.</b> A hand-written projection is exactly the shape that goes
    /// stale the day a field is added: the new weight would silently not be hashed, and a rebalance
    /// would replay as if nothing moved. Walking the declared members makes adding a field to the record
    /// equivalent to adding it to the hash.
    /// <c>Every_declared_profile_member_appears_in_the_canonical_form</c> pins that.</para>
    /// </summary>
    public static string Canonical(CombatAiProfile profile)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        var builder = new System.Text.StringBuilder();
        Append(builder, profile);
        return builder.ToString();
    }

    static void Append(System.Text.StringBuilder builder, object? value)
    {
        switch (value)
        {
            case null:
                builder.Append("N;");
                return;
            case string text:
                builder.Append(text.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(':').Append(text).Append(';');
                return;
            case Enum member:
                builder.Append(member.GetType().Name).Append('.').Append(member.ToString()).Append(';');
                return;
            case bool flag:
                builder.Append(flag ? "T;" : "F;");
                return;
            case IFormattable scalar:
                builder.Append(Convert.ToString(scalar, System.Globalization.CultureInfo.InvariantCulture))
                    .Append(';');
                return;
        }

        if (TryReadOnlyDictionaryEntries(value, out var entries))
        {
            entries.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            builder.Append('{');
            foreach (var (key, item) in entries)
            {
                builder.Append(key.Length.ToString(System.Globalization.CultureInfo.InvariantCulture))
                    .Append(':').Append(key).Append('=');
                Append(builder, item);
            }
            builder.Append("};");
            return;
        }

        if (value is System.Collections.IEnumerable sequence)
        {
            builder.Append('[');
            foreach (var item in sequence) Append(builder, item);
            builder.Append("];");
            return;
        }

        // A record: every public property by name, sorted ordinal so reflection order cannot matter.
        builder.Append('<');
        foreach (var property in value.GetType()
                     .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                     .OrderBy(p => p.Name, StringComparer.Ordinal))
        {
            builder.Append(property.Name).Append('=');
            Append(builder, property.GetValue(value));
        }
        builder.Append(">;");
    }

    /// <summary>`IReadOnlyDictionary&lt;K,V&gt;` is not `IDictionary`, so the entries are read through
    /// the interface's own `Keys` and indexer rather than a cast.</summary>
    static bool TryReadOnlyDictionaryEntries(object value, out List<(string Key, object? Value)> entries)
    {
        entries = new List<(string, object?)>();
        var type = value.GetType();
        var contract = type.GetInterfaces().FirstOrDefault(i =>
            i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>));
        if (contract is null) return false;

        if (type.GetProperty("Keys")?.GetValue(value) is not System.Collections.IEnumerable keys) return false;
        var indexer = contract.GetProperty("Item");
        if (indexer is null) return false;

        foreach (var key in keys)
        {
            entries.Add((Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture) ?? "",
                indexer.GetValue(value, new object?[] { key })));
        }
        return true;
    }
}
