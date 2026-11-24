namespace BorgStudio.Server.Web.Localization;

/// <summary>
/// Languages of the web interface. The browser's language decides; anything else falls back to English.
/// Texts live in Resources/Strings.resx (English) and Strings.&lt;culture&gt;.resx.
/// </summary>
public static class SupportedCultures
{
    public const string Default = "en";

    public static readonly string[] Names = [Default, "de"];
}
