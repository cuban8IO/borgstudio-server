using System.Globalization;

using BorgStudio.Server.Web.Resources;

namespace BorgStudio.Server.Web.Localization;

/// <summary>How the pages write durations, percentages, ratios and dates.</summary>
public static class Formats
{
    public static string Duration(TimeSpan duration) => duration.TotalSeconds switch
    {
        < 60 => string.Format(CultureInfo.CurrentCulture, Strings.DurationSeconds, (int)Math.Round(duration.TotalSeconds)),
        < 3600 => string.Format(CultureInfo.CurrentCulture, Strings.DurationMinutes, (int)duration.TotalMinutes, duration.Seconds),
        _ => string.Format(CultureInfo.CurrentCulture, Strings.DurationHours, (int)duration.TotalHours, duration.Minutes),
    };

    public static string Duration(double seconds) => Duration(TimeSpan.FromSeconds(seconds));

    public static string Percent(double? value) => value is { } v ? (v / 100).ToString("0.#%", CultureInfo.CurrentCulture) : "–";

    public static string Ratio(double? value) => value is { } v ? $"{v.ToString("0.0", CultureInfo.CurrentCulture)} : 1" : "–";

    public static string Day(DateOnly day) => day.ToString("d", CultureInfo.CurrentCulture);

    public static string Month(DateOnly day) => day.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

    public static string When(DateTimeOffset? at) => at is { } value ? value.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) : "–";

    /// <summary>Bytes per second, e.g. "12.5 MB/s".</summary>
    public static string Rate(double bytesPerSecond) => $"{ByteSize.Format((long)bytesPerSecond)}/s";
}
