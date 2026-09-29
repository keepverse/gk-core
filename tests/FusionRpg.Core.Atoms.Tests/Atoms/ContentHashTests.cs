using System.Security.Cryptography;
using System.Text;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// E8 (spec-content-hash.md, definitions §8). The algorithm half — canonical form, sorted
/// concatenation, the registry, and the replay verdict. The database half lives in the data tests.
///
/// <para>The expectations here are built from the <b>spec's rules</b>, not from what the
/// implementation happens to produce: the canonical byte string is assembled by hand in the test and
/// hashed with the framework, so a change of mind in <see cref="ContentHash"/> shows up as a failure
/// rather than being ratified by an oracle that copied it.</para>
/// </summary>
public class ContentHashTests
{
    static readonly ContentHashColumn A = ContentHashColumn.Text("a");
    static readonly ContentHashColumn B = ContentHashColumn.Text("b");
    static readonly ContentHashColumn J = ContentHashColumn.Json("j");

    static string Row(params object?[] values) =>
        ContentHash.Hex(ContentHash.RowDigest(new[] { A, B }, values));

    /// <summary>SHA-256 of a byte string built straight from the spec's rule, with no shared code.</summary>
    static string Sha(params byte[][] parts)
    {
        var all = parts.SelectMany(p => p).ToArray();
        return Convert.ToHexString(SHA256.HashData(all)).ToLowerInvariant();
    }

    static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    // ---- the canonical row form -----------------------------------------------------------------

    [Fact]
    public void A_row_is_its_columns_length_prefixed_in_declared_order()
    {
        // Spec: "columns in declared order, each length-prefixed as {byteLen}:{bytes}".
        var expected = Sha(Utf8("2:hi"), Utf8("5:there"));

        Assert.Equal(expected, Row("hi", "there"));
    }

    [Fact]
    public void A_multi_byte_character_is_prefixed_by_bytes_not_characters()
    {
        // 'é' is two bytes in UTF-8. A character count here would silently make the prefix a lie.
        var expected = Sha(Utf8("2:é"), Utf8("0:"));

        Assert.Equal(expected, Row("é", ""));
    }

    [Fact]
    public void Separator_shifting_between_two_free_text_columns_cannot_forge_a_digest()
    {
        // The exact pair definitions §8 names as the reason a bare 0x1f separator was rejected:
        // both `name` and `power_note` are free text, so the boundary must not be ambiguous.
        Assert.NotEqual(Row("a\u001fb", "c"), Row("a", "b\u001fc"));
    }

    [Fact]
    public void Null_and_empty_string_do_not_collide()
    {
        Assert.NotEqual(Row(null, "x"), Row("", "x"));
    }

    [Fact]
    public void Null_and_a_single_nul_character_string_do_not_collide()
    {
        // definitions §8 encodes NULL as a literal 0x00 and claims the length prefix separates it
        // from a string containing one. It does not: both are one byte of 0x00 with prefix "1:".
        // A column holding exactly "\0" would forge the digest of a NULL column.
        Assert.NotEqual(Row(null, "x"), Row("\0", "x"));
    }

    [Fact]
    public void Null_is_a_sentinel_no_value_length_can_produce()
    {
        // "N" is not a digit, so no {byteLen} prefix can ever spell it. That is what makes a NULL
        // column unforgeable rather than merely unlikely to be forged.
        Assert.Equal(Sha(Utf8("N:"), Utf8("1:x")), Row(null, "x"));
    }

    [Fact]
    public void A_column_count_mismatch_throws_rather_than_hashing_a_short_row()
    {
        Assert.Throws<ArgumentException>(() =>
            ContentHash.RowDigest(new[] { A, B }, new object?[] { "only-one" }));
    }

    [Fact]
    public void Numbers_and_booleans_are_stringified_invariantly()
    {
        // A row read back from SQLite arrives as long/string, never as the C# type it was written as.
        Assert.Equal(Row("7", "1"), Row(7L, true));
    }

    // ---- table digest ---------------------------------------------------------------------------

    [Fact]
    public void An_empty_covered_table_digests_as_the_sha256_of_nothing()
    {
        // The published SHA-256 of the empty input — an independent constant, not one this code made.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            ContentHash.Hex(ContentHash.TableDigest(Array.Empty<byte[]>())));
    }

    [Fact]
    public void Row_order_does_not_change_the_table_digest()
    {
        var r1 = ContentHash.RowDigest(new[] { A, B }, new object?[] { "one", "1" });
        var r2 = ContentHash.RowDigest(new[] { A, B }, new object?[] { "two", "2" });
        var r3 = ContentHash.RowDigest(new[] { A, B }, new object?[] { "three", "3" });

        Assert.Equal(
            ContentHash.Hex(ContentHash.TableDigest(new[] { r1, r2, r3 })),
            ContentHash.Hex(ContentHash.TableDigest(new[] { r3, r1, r2 })));
    }

    [Fact]
    public void A_duplicated_row_changes_the_table_digest()
    {
        // THE reason XOR-fold is banned: under a fold these two are equal, and an import that
        // doubled every row would report "nothing changed" while the database doubled.
        var r = ContentHash.RowDigest(new[] { A, B }, new object?[] { "one", "1" });

        Assert.NotEqual(
            ContentHash.Hex(ContentHash.TableDigest(new[] { r })),
            ContentHash.Hex(ContentHash.TableDigest(new[] { r, r })));
    }

    [Fact]
    public void One_changed_character_changes_the_table_digest()
    {
        var before = ContentHash.RowDigest(new[] { A, B }, new object?[] { "amount", "45" });
        var after = ContentHash.RowDigest(new[] { A, B }, new object?[] { "amount", "46" });

        Assert.NotEqual(
            ContentHash.Hex(ContentHash.TableDigest(new[] { before })),
            ContentHash.Hex(ContentHash.TableDigest(new[] { after })));
    }

    [Fact]
    public void Table_order_changes_the_combined_hash()
    {
        var x = ContentHash.TableDigest(new[] { ContentHash.RowDigest(new[] { A }, new object?[] { "x" }) });
        var y = ContentHash.TableDigest(new[] { ContentHash.RowDigest(new[] { A }, new object?[] { "y" }) });

        Assert.NotEqual(
            ContentHash.Hex(ContentHash.Combine(new[] { x, y })),
            ContentHash.Hex(ContentHash.Combine(new[] { y, x })));
    }

    // ---- canonical JSON -------------------------------------------------------------------------

    [Theory]
    [InlineData("""{"b":1,"a":2}""", """{"a":2,"b":1}""")]                  // keys sorted ordinal
    [InlineData("""{ "a" : 2 ,  "b" : 1 }""", """{"a":2,"b":1}""")]         // whitespace irrelevant
    [InlineData("""{"a":100.0}""", """{"a":100}""")]                        // integral -> integer
    [InlineData("""{"a":1.50}""", """{"a":1.5}""")]                         // trailing zeros dropped
    [InlineData("""{"a":[3,1,2]}""", """{"a":[3,1,2]}""")]                  // array order is content
    [InlineData("""{"B":1,"a":1}""", """{"B":1,"a":1}""")]                  // ordinal: 'B' < 'a'
    public void Canonical_json_normalises_form_but_never_meaning(string input, string expected)
    {
        Assert.Equal(expected, ContentHash.CanonicalJson(input));
    }

    [Fact]
    public void Json_columns_hash_equal_across_key_order_and_whitespace()
    {
        var tidy = ContentHash.RowDigest(new[] { J }, new object?[] { """{"amount":45,"channel":"maxHp"}""" });
        var messy = ContentHash.RowDigest(new[] { J }, new object?[] { """{ "channel" : "maxHp",  "amount" : 45 }""" });

        Assert.Equal(ContentHash.Hex(tidy), ContentHash.Hex(messy));
    }

    [Fact]
    public void A_changed_json_value_still_changes_the_digest()
    {
        var before = ContentHash.RowDigest(new[] { J }, new object?[] { """{"amount":45}""" });
        var after = ContentHash.RowDigest(new[] { J }, new object?[] { """{"amount":46}""" });

        Assert.NotEqual(ContentHash.Hex(before), ContentHash.Hex(after));
    }

    [Fact]
    public void Unparseable_json_is_hashed_as_text_rather_than_throwing()
    {
        // A database edited by hand must produce a hash an operator can compare, not an exception
        // on the boot path. The row validators are what refuse malformed JSON.
        var digest = ContentHash.RowDigest(new[] { J }, new object?[] { "{not json" });

        Assert.Equal(64, ContentHash.Hex(digest).Length);
    }

    [Fact]
    public void A_json_column_and_a_text_column_holding_the_same_bytes_agree_when_already_canonical()
    {
        var asJson = ContentHash.RowDigest(new[] { J }, new object?[] { """{"a":1}""" });
        var asText = ContentHash.RowDigest(new[] { ContentHashColumn.Text("j") }, new object?[] { """{"a":1}""" });

        Assert.Equal(ContentHash.Hex(asJson), ContentHash.Hex(asText));
    }

    // ---- the registry ---------------------------------------------------------------------------

    [Fact]
    public void Registry_order_is_the_table_name_ordinal()
    {
        var names = ContentHashRegistry.Current.Select(t => t.TableName).ToList();

        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
    }

    [Fact]
    public void Version_one_covers_the_tables_that_exist_and_no_player_state()
    {
        var names = ContentHashRegistry.For(1).Select(t => t.TableName).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(new[]
        {
            "effect_atom", "effect_container", "effect_container_atom",
            "effect_container_pool", "effect_curve", "rarity",
        }.ToHashSet(StringComparer.Ordinal), names);

        // Content is hashed; player state is not. content_meta holds the revision, not content.
        Assert.DoesNotContain("effect_instance", names);
        Assert.DoesNotContain("effect_instance_atom", names);
        Assert.DoesNotContain("effect_binding", names);
        Assert.DoesNotContain("content_meta", names);
    }

    [Fact]
    public void An_unknown_registry_version_is_refused_rather_than_defaulted()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ContentHashRegistry.For(99));
    }

    [Fact]
    public void Every_covered_table_declares_at_least_one_column_and_no_duplicates()
    {
        foreach (var t in ContentHashRegistry.Current)
        {
            Assert.NotEmpty(t.Columns);
            Assert.Equal(t.Columns.Count,
                t.Columns.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        }
    }

    // ---- the stamp and the replay verdict --------------------------------------------------------

    static ContentHashStamp Stamp(int version, string hash, params (string Table, string Digest)[] tables) =>
        new(version, hash, tables.ToDictionary(t => t.Table, t => t.Digest, StringComparer.Ordinal));

    [Fact]
    public void A_stamp_round_trips_through_its_compact_form()
    {
        var original = Stamp(1, new string('a', 64), ("effect_atom", new string('b', 64)), ("rarity", new string('c', 64)));

        Assert.True(ContentHashStamp.TryParse(original.ToCompact(), out var back));
        Assert.Equal(1, back.SchemaVersion);
        Assert.Equal(original.Hash, back.Hash);
        Assert.Equal(new string('b', ContentHashStamp.TableDigestHexLength), back.TableDigests["effect_atom"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData("1|abc|x=y")]        // no 'v'
    [InlineData("vX|abc|x=y")]       // unparseable version
    [InlineData("v1|abc")]           // missing the table section
    [InlineData("v1||x=y")]          // empty hash
    [InlineData("v1|abc|=y")]        // nameless table
    public void A_malformed_compact_stamp_does_not_parse(string compact)
    {
        Assert.False(ContentHashStamp.TryParse(compact, out _));
    }

    [Fact]
    public void The_short_form_is_what_a_human_reads_in_a_report()
    {
        Assert.Equal("content:a3f91c", Stamp(1, "a3f91c" + new string('0', 58)).Short);
    }

    [Fact]
    public void No_stored_stamp_is_a_match_so_rows_older_than_this_module_still_heal()
    {
        var v = ContentHashComparison.Compare(null, Stamp(1, "abc"));

        Assert.Equal(ContentHashVerdict.Match, v.Verdict);
        Assert.False(v.ShouldRefuse);
    }

    [Fact]
    public void An_identical_stamp_matches()
    {
        var current = Stamp(1, "abc", ("effect_atom", "1111111111111111"));

        Assert.Equal(ContentHashVerdict.Match, ContentHashComparison.Compare(current.ToCompact(), current).Verdict);
    }

    [Fact]
    public void A_different_hash_at_the_same_registry_version_is_refused_and_names_both()
    {
        var stored = Stamp(1, "aaa" + new string('0', 61), ("effect_atom", "1111111111111111"));
        var current = Stamp(1, "bbb" + new string('0', 61), ("effect_atom", "2222222222222222"));

        var v = ContentHashComparison.Compare(stored.ToCompact(), current);

        Assert.Equal(ContentHashVerdict.Mismatch, v.Verdict);
        Assert.True(v.ShouldRefuse);
        Assert.Contains(stored.Hash, v.Reason);
        Assert.Contains(current.Hash, v.Reason);
        Assert.Equal(new[] { "effect_atom" }, v.ChangedTables);
    }

    [Fact]
    public void A_mismatch_names_only_the_tables_that_actually_moved()
    {
        var stored = Stamp(1, "aaa", ("effect_atom", "1111111111111111"), ("rarity", "9999999999999999"));
        var current = Stamp(1, "bbb", ("effect_atom", "2222222222222222"), ("rarity", "9999999999999999"));

        Assert.Equal(new[] { "effect_atom" }, ContentHashComparison.Compare(stored.ToCompact(), current).ChangedTables);
    }

    [Fact]
    public void A_registry_version_change_is_reported_but_not_refused()
    {
        // E18 registers three element tables at build position 14 and E9 two more at 15 — after E11
        // has already stamped the Checkpoint D corpus. A blanket refusal would hard-fail all of it.
        var stored = Stamp(1, "aaa", ("effect_atom", "1111111111111111"));
        var current = Stamp(2, "bbb", ("effect_atom", "1111111111111111"), ("effect_element", "3333333333333333"));

        var v = ContentHashComparison.Compare(stored.ToCompact(), current);

        Assert.Equal(ContentHashVerdict.RegistryChanged, v.Verdict);
        Assert.False(v.ShouldRefuse);
        Assert.Equal(new[] { "effect_element" }, v.AddedTables);
        Assert.Empty(v.RemovedTables);
        Assert.Empty(v.ChangedTables);
        Assert.Contains("1 -> 2", v.Reason);
    }

    [Fact]
    public void Across_registry_versions_the_shared_tables_are_still_compared()
    {
        // The whole point of carrying per-table digests: a version bump must not become a blind spot
        // where an edited atom rides along unnoticed.
        var stored = Stamp(1, "aaa", ("effect_atom", "1111111111111111"));
        var current = Stamp(2, "bbb", ("effect_atom", "2222222222222222"), ("effect_element", "3333333333333333"));

        var v = ContentHashComparison.Compare(stored.ToCompact(), current);

        Assert.Equal(ContentHashVerdict.RegistryChanged, v.Verdict);
        Assert.Equal(new[] { "effect_atom" }, v.ChangedTables);
        Assert.Contains("effect_atom", v.Reason);
    }

    [Fact]
    public void A_table_leaving_the_registry_is_reported_as_removed()
    {
        var stored = Stamp(1, "aaa", ("effect_atom", "1111111111111111"), ("legacy_table", "4444444444444444"));
        var current = Stamp(2, "bbb", ("effect_atom", "1111111111111111"));

        var v = ContentHashComparison.Compare(stored.ToCompact(), current);

        Assert.Equal(new[] { "legacy_table" }, v.RemovedTables);
        Assert.Empty(v.AddedTables);
    }

    [Fact]
    public void An_unreadable_stamp_refuses_rather_than_assuming_it_matched()
    {
        var v = ContentHashComparison.Compare("this is not a stamp", Stamp(1, "abc"));

        Assert.Equal(ContentHashVerdict.Unreadable, v.Verdict);
        Assert.True(v.ShouldRefuse);
    }
}
