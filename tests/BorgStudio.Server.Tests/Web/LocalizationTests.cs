using System.Collections;
using System.Globalization;
using System.Resources;

using BorgStudio.Server.Web.Localization;
using BorgStudio.Server.Web.Resources;

namespace BorgStudio.Server.Tests.Web;

public sealed class LocalizationTests
{
    public static TheoryData<string> Translations =>
        new(SupportedCultures.Names.Where(name => name != SupportedCultures.Default));

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryText_IsTranslated(string culture)
    {
        var english = Read(CultureInfo.InvariantCulture);
        var translated = Read(CultureInfo.GetCultureInfo(culture));

        Assert.Empty(english.Keys.Except(translated.Keys));
        Assert.Empty(translated.Keys.Except(english.Keys));
        Assert.All(translated, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), entry.Key));
    }

    [Theory]
    [MemberData(nameof(Translations))]
    public void Placeholders_Match(string culture)
    {
        var english = Read(CultureInfo.InvariantCulture);
        var translated = Read(CultureInfo.GetCultureInfo(culture));

        foreach (var (key, text) in english)
        {
            Assert.True(
                Placeholders(text).SetEquals(Placeholders(translated[key])),
                $"{key}: placeholders differ between English and {culture}.");
        }
    }

    private static Dictionary<string, string> Read(CultureInfo culture)
    {
        var set = Strings.ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)
            ?? throw new InvalidOperationException($"No texts for {culture.Name}.");
        return set.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static HashSet<string> Placeholders(string text) =>
        System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).ToHashSet();
}
