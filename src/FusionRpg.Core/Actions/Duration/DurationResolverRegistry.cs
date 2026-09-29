namespace FusionRpg.Core.Actions.Duration;

public sealed class NoDurationResolverRegisteredException : Exception
{
    public NoDurationResolverRegisteredException(string mode)
        : base($"no IDurationResolver registered for mode '{mode}' — never silently defaults to ticks") { }
}

/// <summary>
/// T28's mode dispatch (spec-duration-resolver.md §4's per-mode table: <c>battle</c> BLOCKED on
/// `P0.5`, <c>lawn</c> open/deferred). A missing mode <b>throws naming the mode</b> — never a silent
/// fallback — because a resolver that quietly returned the raw turn count as ticks would be wrong in
/// every mode at once and look like it worked.
///
/// <para>Registration is additive and mode-keyed, so the day <c>BattleDurationResolver</c> or a lawn
/// resolver actually lands, this class needs no change — only a new <see cref="Register"/> call at
/// whatever bootstrap wires it in.</para>
/// </summary>
public sealed class DurationResolverRegistry
{
    readonly Dictionary<string, IDurationResolver> _byMode = new(StringComparer.Ordinal);

    public void Register(string mode, IDurationResolver resolver)
    {
        if (string.IsNullOrWhiteSpace(mode)) throw new ArgumentException("mode required", nameof(mode));
        ArgumentNullException.ThrowIfNull(resolver);
        _byMode[mode] = resolver;
    }

    public IDurationResolver Resolve(string mode) =>
        _byMode.TryGetValue(mode, out var resolver) ? resolver : throw new NoDurationResolverRegisteredException(mode);
}
