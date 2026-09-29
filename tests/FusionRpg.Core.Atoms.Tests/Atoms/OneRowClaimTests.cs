using FusionRpg.Contracts;
using FusionRpg.Core.Effects;
using FusionRpg.Core.Effects.Atoms;
using Xunit;

namespace FusionRpg.Core.Tests.Atoms;

/// <summary>
/// <b>Checkpoint D.</b> The claim the whole program was built to make:
///
/// <blockquote>A new effect using an existing kind costs one row, and no build.</blockquote>
///
/// <para>It lives here because this is where the claim is made — E14b only re-runs it later as a
/// regression. Every earlier module was an argument that this would be true; these are the tests
/// that decide it.</para>
///
/// <para><b>What "no build" can and cannot be asserted from inside a running process.</b> A test that
/// already loaded Core cannot prove Core was not rebuilt. So it does not pretend to: it asserts the
/// behavioural half — a row nobody compiled against becomes a grantable, firing effect — and the
/// no-rebuild half is held by this file referencing no new source to add the effect. Saying that
/// plainly is the alternative to quietly relaxing the claim.</para>
/// </summary>
public class OneRowClaimTests
{
    /// <summary>One row. This is the entire cost of the new effect.</summary>
    const string NewEffectRow = """
        {
          "schemaVersion": 1,
          "kind": "atom",
          "entries": [
            {
              "family": "atom.fx-scorch-on-hit",
              "tier": 1,
              "kind": "status.apply",
              "name": "Scorch on hit",
              "icdKey": "fx.scorch_on_hit",
              "when": { "trigger": "OnDamageDealt" },
              "params": { "status": "butter", "duration": 7 }
            }
          ]
        }
        """;

    static List<EffectDef> CatalogWith(string seedFile)
    {
        var collected = AtomSeedFile.Collect(new[] { ("new-effect.json", seedFile) });
        Assert.True(collected.IsOk, string.Join("; ", collected.Errors));

        var compiled = AtomCompiler.Compile(
            collected.Content.Atoms, RuntimeId.Lawn, 1, hostIsPlanner: true);
        Assert.Empty(compiled.Rejected);

        return compiled.Defs.Select(AtomPushCodec.ToDef).ToList();
    }

    [Fact]
    public void One_row_becomes_a_grantable_effect_that_fires()
    {
        // No new kind, no new opcode, no new C# type, no new case in any switch. A row.
        var host = new SimEffectHost(seed: 1, catalog: CatalogWith(NewEffectRow));

        host.Grant(new EffectGrantDto
        {
            GrantId = "g-new",
            EffectId = "fx.scorch_on_hit",
            OwnerKey = "match",
        });

        var plan = host.OnEvent(Hit());

        var action = Assert.Single(plan.Actions);
        Assert.Equal(EffectActions.ApplyStatus, action.Action);
        Assert.Equal("fx.scorch_on_hit", action.EffectId);
        Assert.Equal("g-new", action.GrantId);
        Assert.Equal("butter", action.Params["status"]?.ToString());
    }

    [Fact]
    public void The_row_carries_its_own_magnitude_rather_than_inheriting_one()
    {
        // A row that could not set its own numbers would be a template, not an effect.
        var host = new SimEffectHost(seed: 1, catalog: CatalogWith(NewEffectRow));
        host.Grant(new EffectGrantDto { GrantId = "g", EffectId = "fx.scorch_on_hit", OwnerKey = "match" });

        var action = Assert.Single(host.OnEvent(Hit()).Actions);

        Assert.Equal("7", action.Params["duration"]?.ToString());
    }

    [Fact]
    public void Editing_the_one_row_changes_the_effect_and_nothing_else()
    {
        // The claim is about the cost of a CHANGE too, not only of a creation.
        var edited = NewEffectRow.Replace("\"duration\": 7", "\"duration\": 11", StringComparison.Ordinal);
        var host = new SimEffectHost(seed: 1, catalog: CatalogWith(edited));
        host.Grant(new EffectGrantDto { GrantId = "g", EffectId = "fx.scorch_on_hit", OwnerKey = "match" });

        var action = Assert.Single(host.OnEvent(Hit()).Actions);

        Assert.Equal("11", action.Params["duration"]?.ToString());
    }

    [Fact]
    public void An_unknown_effect_id_still_fails_loudly()
    {
        // Proves the test above is not passing because the bag accepts anything. Without this, "the
        // row worked" and "nothing is checked" look identical.
        var host = new SimEffectHost(seed: 1, catalog: CatalogWith(NewEffectRow));

        Assert.ThrowsAny<Exception>(() => host.Grant(new EffectGrantDto
        {
            GrantId = "g-missing",
            EffectId = "fx.no_such_effect",
            OwnerKey = "match",
        }));
    }

    [Fact]
    public void A_row_naming_a_kind_that_does_not_exist_is_refused_not_ignored()
    {
        // "One row" must not mean "any row". The closed vocabulary is the other half of the claim:
        // an effect is cheap to add precisely because what it may say is fixed.
        var bad = NewEffectRow.Replace("\"kind\": \"status.apply\"", "\"kind\": \"status.scorch\"",
            StringComparison.Ordinal);
        var collected = AtomSeedFile.Collect(new[] { ("bad.json", bad) });

        var verdict = AtomRowValidator.Validate(collected.Content.Atoms[0]);

        Assert.Equal(AtomRejectionReason.UnknownKind, verdict.Reason);
    }

    [Fact]
    public void The_new_effect_needed_no_new_source_file()
    {
        // The no-rebuild half, as far as a running process can honestly speak to it: the effect above
        // was created from a string in this file and a compiler that already existed. If adding it
        // had required a new kind, opcode or sink arm, one of the tests above could not compile.
        var kinds = AtomKindRegistry.All.Select(k => k.KindId).ToList();

        Assert.Contains("status.apply", kinds);
        Assert.Equal(AtomKindRegistry.KindCount, kinds.Count);
    }

    static EffectEventDto Hit() => new()
    {
        Trigger = EffectTriggers.OnDamageDealt,
        ActorPtr = "0xA",
        TargetPtr = "0xB",
        Side = "zombie",
        TypeId = 3,
    };
}
