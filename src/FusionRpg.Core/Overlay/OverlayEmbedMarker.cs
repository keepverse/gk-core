namespace FusionRpg.Core.Overlay;

/// <summary>
/// The **embed marker** both hosts add to the overlay URL at navigate time (rift-gate decision 16), so
/// the page can tell it is embedded and therefore that Leave can work.
///
/// It is a **query flag**, not a hash fragment: the FE uses a HashRouter, so the hash is the router's
/// own domain and a marker there would be parsed as a route. (The launcher already uses query flags
/// for its own settings switches.)
///
/// Same-origin is unaffected: <see cref="OverlayViewPolicy.IsSameOrigin"/> compares only scheme/host/port.
///
/// A plain browser visit carries no marker, and the page must behave correctly without one — the Leave
/// control is simply not offered.
/// </summary>
public static class OverlayEmbedMarker
{
    /// <summary>The query key. Also read by the web FE (<c>overlayEmbed.ts</c>); a guard pins both sides.</summary>
    public const string QueryKey = "embed";

    /// <summary>The query value that marks an embedded visit.</summary>
    public const string QueryValue = "1";

    /// <summary>
    /// The URL to navigate to, with the marker applied. Idempotent, and safe on a URL that already has
    /// a query string (it appends with <c>&amp;</c> rather than a second <c>?</c>).
    /// </summary>
    public static string MarkedUrl(string? baseUrl)
    {
        var url = baseUrl ?? "";
        if (url.Length == 0) return url;
        if (IsMarked(url)) return url;

        var separator = url.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        return $"{url}{separator}{QueryKey}={QueryValue}";
    }

    /// <summary>True when this URL already carries the marker (query position only, never the fragment).</summary>
    public static bool IsMarked(string? url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        var queryStart = url.IndexOf('?', StringComparison.Ordinal);
        if (queryStart < 0) return false;

        var fragmentStart = url.IndexOf('#', queryStart);
        var query = fragmentStart < 0 ? url[(queryStart + 1)..] : url[(queryStart + 1)..fragmentStart];

        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) continue;
            if (pair[..eq] == QueryKey && pair[(eq + 1)..] == QueryValue) return true;
        }
        return false;
    }
}
