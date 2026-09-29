namespace FusionRpg.Core.Effects.Atoms;

/// <summary>
/// The narrow "does this atom still mean the same executable thing" test <c>durable-ownership</c>
/// (item module 1, item-ideal.md R2) needs to replace the blunt whole-instance <c>catalog_revision</c>
/// equality check with a per-atom one.
///
/// <para><b>Deliberately narrow, and it is a design choice, not an oversight (D32).</b> A balance
/// edit to <c>params_json</c> or <c>when_json</c> is meant to reach circulating gear — that is what
/// D32 rules when it keeps <see cref="RuntimeId"/>-facing bind resolution reading the live catalog
/// rather than freezing <c>InstanceAtomRow.ValuesJson</c> at bind time. The only field whose change
/// makes reusing a frozen binding unsafe is <c>kind_id</c>: it decides which executor interprets the
/// row's own <c>when_json</c>/<c>params_json</c>, so a kind change under the same <c>atom_id</c> is a
/// different runtime contract, not a bigger or smaller number.</para>
///
/// <para>Uses <see cref="ContentHash"/>'s own canonical-encoding machinery rather than a bespoke
/// string compare, so this stays correct if the identity definition ever needs to widen (a column-list
/// edit, exactly like <see cref="ContentHashRegistry"/>'s own versioning story) instead of a rewritten
/// comparison.</para>
/// </summary>
public static class AtomIdentityDigest
{
    static readonly IReadOnlyList<ContentHashColumn> Columns = new[]
    {
        ContentHashColumn.Text("kind_id"),
    };

    /// <summary>Hex digest stored on the instance-atom row at roll time (<see cref="Instantiator"/>).</summary>
    public static string Of(AtomRow atom) =>
        ContentHash.Hex(ContentHash.RowDigest(Columns, new object?[] { atom.KindId }));
}
