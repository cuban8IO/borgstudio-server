using System.Globalization;

namespace BorgStudio.Server.Web.Localization;

/// <summary>
/// Sizes in decimal units (1 GB = 1000³ bytes), the same as borg uses for its quotas, so a quota of
/// "10 GB" here is "10G" for borg.
/// </summary>
public static class ByteSize
{
    public const long BytesPerGigabyte = 1_000_000_000;

    private static readonly string[] Units = ["B", "kB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        double value = bytes;
        var unit = 0;
        while (Math.Abs(value) >= 1000 && unit < Units.Length - 1)
        {
            value /= 1000;
            unit++;
        }

        var format = unit == 0 || value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return $"{value.ToString(format, CultureInfo.CurrentCulture)} {Units[unit]}";
    }

    public static decimal? ToGigabytes(long? bytes) => bytes is { } b ? Math.Round((decimal)b / BytesPerGigabyte, 3) : null;

    /// <summary>A quota typed in GB; empty or zero means no limit.</summary>
    public static long? FromGigabytes(decimal? gigabytes) =>
        gigabytes is { } gb && gb > 0 ? (long)Math.Round(gb * BytesPerGigabyte) : null;
}
