using System.Globalization;
using System.Net;

using Microsoft.AspNetCore.Components;

namespace BorgStudio.Server.Web.Components.Shared.Charts;

/// <summary>
/// SVG &lt;text&gt; elements for the charts. Razor treats &lt;text&gt; inside code blocks as its own tag,
/// so they are written as markup here, with the content encoded.
/// </summary>
internal static class SvgText
{
    public static MarkupString Of(object x, object y, string anchor, string cssClass, object? content) =>
        new($"<text x=\"{Number(x)}\" y=\"{Number(y)}\" text-anchor=\"{anchor}\" class=\"{cssClass}\">{WebUtility.HtmlEncode(content?.ToString() ?? string.Empty)}</text>");

    private static string Number(object value) => value switch
    {
        double d => d.ToString("0.#", CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        string s => s,
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "0",
    };
}
